using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Build.Locator;
using Neo4j.Driver;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace CodeAnalyzer
{
    class Program
    {
        // Node color mappings
        static readonly Dictionary<string, string> NodeColors = new()
        {
            { "Class", "#4287f5" },
            { "Interface", "#f542f5" },
            { "Method", "#42f54e" },
            { "HttpCall", "#f54242" },
            { "MongoCollection", "#f5a442" },
            { "ExternalService", "#f5f542" },
            { "Root", "orange" }
        };

        // Queue to handle pending MongoDB links
        private static ConcurrentQueue<(string MethodId, string CollectionId)> pendingMongoLinks = new();

        // Class-level variable for baseUrl
        private static string BaseUrl;

        static async Task Main(string[] args)
        {
            // Initialize BaseUrl at class level
            BaseUrl = Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost";
            string rootName = Environment.GetEnvironmentVariable("ROOT_NAME") ?? "UnknownRoot";
            string solutionPath = @"/app/" + rootName + ".sln";

            // Initialize Neo4j driver with retry logic
            var driver = await CreateNeo4jDriverWithRetry("bolt://host.docker.internal:7687", AuthTokens.Basic("neo4j", "securepassword"));
            using var session = driver.AsyncSession();

            // Clean up previous run data
            await CleanupPreviousRunData(session, rootName);

            // Create uniqueness constraints in Neo4j
            await CreateUniquenessConstraints(session);

            // Create root node
            string rootId = $"root:{rootName}";
            await CreateNode(session, rootId, "Root", new Dictionary<string, object>
            {
                { "name", rootName },
                { "project", rootName },
                { "color", NodeColors["Root"] }
            });

            // Register MSBuild
            MSBuildLocator.RegisterDefaults();
            var workspace = MSBuildWorkspace.Create();

            // Open solution
            var solution = await workspace.OpenSolutionAsync(solutionPath);

            // Process each project in the solution
            foreach (var project in solution.Projects)
            {
                await ProcessProject(project, session, rootId);
            }

            // Process pending MongoDB links
            await ProcessPendingMongoLinks(session);

            await session.CloseAsync();
        }

        static async Task ProcessProject(Project project, IAsyncSession session, string rootId)
        {
            string projectName = project.Name;

            foreach (var document in project.Documents)
            {
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null) continue;

                var root = await syntaxTree.GetRootAsync();

                var classDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .Where(td => td is ClassDeclarationSyntax || td is InterfaceDeclarationSyntax);

                foreach (var typeDeclaration in classDeclarations)
                {
                    await ProcessType(typeDeclaration, semanticModel, session, projectName, document, rootId);
                }
            }
        }

        static async Task ProcessType(TypeDeclarationSyntax typeDeclaration, SemanticModel semanticModel, IAsyncSession session, string projectName, Document document, string rootId)
        {
            string typeName = typeDeclaration.Identifier.Text;
            string namespaceName = GetFullNamespace(typeDeclaration);

            // Determine if this is an interface or class
            var symbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as ITypeSymbol;
            bool isInterface = symbol?.TypeKind == TypeKind.Interface;

            string label = isInterface ? "Interface" : "Class";
            string typeId = $"{projectName}:{namespaceName}.{typeName}";

            // Create or merge Class/Interface node
            await CreateNode(session, typeId, label, new Dictionary<string, object>
            {
                { "name", typeName },
                { "namespace", namespaceName },
                { "project", projectName },
                { "file", document.FilePath },
                { "color", NodeColors[label] },
                { "url", CreateUrl(document.FilePath, typeDeclaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1) }
            });

            // Link Root to this class/interface
            await CreateRelationship(session, rootId, typeId, "CONTAINS");

            // Process base classes and interfaces
            if (typeDeclaration.BaseList != null)
            {
                foreach (var baseType in typeDeclaration.BaseList.Types)
                {
                    await ProcessBaseType(baseType, session, projectName, typeId, semanticModel);
                }
            }

            // Process method declarations
            var methodDeclarations = typeDeclaration.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methodDeclarations)
            {
                await ProcessMethod(method, semanticModel, session, projectName, document, typeId);
            }
        }

        static async Task ProcessBaseType(BaseTypeSyntax baseType, IAsyncSession session, string projectName, string derivedId, SemanticModel semanticModel)
        {
            var typeSymbol = semanticModel.GetSymbolInfo(baseType.Type).Symbol as ITypeSymbol;
            if (typeSymbol == null) return;

            string baseTypeName = typeSymbol.Name;
            string baseNamespaceName = typeSymbol.ContainingNamespace.ToDisplayString();
            string baseProjectName = typeSymbol.ContainingAssembly?.Name ?? projectName; // Might be external
            string baseTypeId = $"{baseProjectName}:{baseNamespaceName}.{baseTypeName}";

            // Determine if base type is a class or interface
            bool isInterface = typeSymbol.TypeKind == TypeKind.Interface;
            string label = isInterface ? "Interface" : "Class";

            // Create or merge base node
            await CreateNode(session, baseTypeId, label, new Dictionary<string, object>
            {
                { "name", baseTypeName },
                { "namespace", baseNamespaceName },
                { "project", baseProjectName },
                { "color", NodeColors[label] }
            });

            // Create relationship depending on interface or class
            string relationship = isInterface ? "IMPLEMENTS" : "INHERITS_FROM";
            await CreateRelationship(session, derivedId, baseTypeId, relationship);
        }

        static async Task ProcessMethod(MethodDeclarationSyntax method, SemanticModel semanticModel, IAsyncSession session, string projectName, Document document, string classId)
        {
            string methodName = method.Identifier.Text;
            string methodSignature = $"{methodName}({string.Join(", ", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "object"))})";
            string methodId = $"{classId}.{methodSignature}";

            // Create or merge Method node
            await CreateNode(session, methodId, "Method", new Dictionary<string, object>
            {
                { "name", methodName },
                { "signature", methodSignature },
                { "classId", classId },
                { "project", projectName },
                { "file", document.FilePath },
                { "color", NodeColors["Method"] },
                { "url", CreateUrl(document.FilePath, method.GetLocation().GetLineSpan().StartLinePosition.Line + 1) }
            });

            // Link Class to Method
            await CreateRelationship(session, classId, methodId, "DECLARES");

            // Process method body or expression body
            var body = (SyntaxNode)method.Body ?? method.ExpressionBody;
            if (body != null)
            {
                // Process method invocations
                var methodInvocations = body.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var invocation in methodInvocations)
                {
                    var resolvedMethod = ResolveMethod(invocation, semanticModel);
                    if (resolvedMethod != null && IsUsefulLink("Method", "Method", "CALLS"))
                    {
                        // Create or merge the target method node if minimal info is available
                        await CreateNode(session, resolvedMethod.Value.MethodId, "Method", new Dictionary<string, object>
                        {
                            { "name", resolvedMethod.Value.MethodName },
                            { "classId", resolvedMethod.Value.ClassId },
                            { "project", ExtractProjectFromId(resolvedMethod.Value.ClassId) }
                        });
                        await CreateRelationship(session, methodId, resolvedMethod.Value.MethodId, "CALLS");
                    }
                }

                // Process HTTP calls
                var httpInvocations = methodInvocations.Where(invocation => IsHttpCall(semanticModel, invocation));
                foreach (var httpCall in httpInvocations)
                {
                    string serviceUrl = ExtractHttpServiceUrl(httpCall, semanticModel);
                    string serviceId = $"service:{serviceUrl}";

                    await CreateNode(session, serviceId, "ExternalService", new Dictionary<string, object>
                    {
                        { "url", serviceUrl },
                        { "color", NodeColors["ExternalService"] }
                    });

                    await CreateRelationship(session, methodId, serviceId, "CALLS_SERVICE");
                }

                // Process MongoDB queries
                var mongoQueries = methodInvocations.Where(invocation => IsMongoDbQuery(semanticModel, invocation));
                foreach (var mongoCall in mongoQueries)
                {
                    string collectionName = ExtractCollectionName(mongoCall, semanticModel);
                    string collectionId = $"mongo:YourDatabaseName.{collectionName}";
                    // Enqueue pending Mongo links to process later
                    pendingMongoLinks.Enqueue((methodId, collectionId));
                }
            }
        }

        static async Task CreateNode(IAsyncSession session, string id, string label, Dictionary<string, object> properties)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("id cannot be null or empty", nameof(id));
            }

            // MERGE with ON CREATE and ON MATCH to handle both creation and update
            string setClause = string.Join(", ", properties.Select(p => $"n.{p.Key} = ${p.Key}"));
            var query = $"MERGE (n:{label} {{id: $id}}) ON CREATE SET {setClause} ON MATCH SET {setClause}";
            properties["id"] = id;
            await session.RunAsync(query, properties);
        }

        static async Task CreateRelationship(IAsyncSession session, string sourceId, string targetId, string relationshipType)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId))
            {
                throw new ArgumentException("SourceId and TargetId cannot be null or empty");
            }

            var query = $"MATCH (a {{id: $sourceId}}), (b {{id: $targetId}}) MERGE (a)-[:{relationshipType}]->(b)";
            await session.RunAsync(query, new { sourceId, targetId });
        }

        static async Task CreateUniquenessConstraints(IAsyncSession session)
        {
            await session.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (c:Class) REQUIRE c.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (i:Interface) REQUIRE i.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (m:Method) REQUIRE m.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (col:MongoCollection) REQUIRE col.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (s:ExternalService) REQUIRE s.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (r:Root) REQUIRE r.id IS UNIQUE");
        }

        static string GetFullNamespace(BaseTypeDeclarationSyntax syntax)
        {
            string namespaceName = string.Empty;
            SyntaxNode potentialNamespaceParent = syntax.Parent;

            while (potentialNamespaceParent != null)
            {
                if (potentialNamespaceParent is NamespaceDeclarationSyntax namespaceParent)
                {
                    var nameSpace = namespaceParent.Name.ToString();
                    namespaceName = string.IsNullOrEmpty(namespaceName) ? nameSpace : $"{nameSpace}.{namespaceName}";
                }
                else if (potentialNamespaceParent is FileScopedNamespaceDeclarationSyntax fileNamespace)
                {
                    namespaceName = fileNamespace.Name.ToString();
                }
                potentialNamespaceParent = potentialNamespaceParent.Parent;
            }

            return namespaceName;
        }

        static async Task<IDriver> CreateNeo4jDriverWithRetry(string uri, IAuthToken auth, int maxRetries = 100, int initialDelay = 5000)
        {
            int delay = initialDelay;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var driver = GraphDatabase.Driver(uri, auth);
                    await using var session = driver.AsyncSession();
                    await session.RunAsync("RETURN 1");
                    Console.WriteLine("Connected to Neo4j");
                    return driver;
                }
                catch (ServiceUnavailableException)
                {
                    Console.WriteLine($"Attempt {attempt + 1}: Neo4j connection failed, retrying in {delay / 1000.0} seconds...");
                    await Task.Delay(delay);
                    delay *= 2; // Exponential backoff
                }
            }
            throw new Exception("Failed to connect to Neo4j after several attempts");
        }

        static async Task CleanupPreviousRunData(IAsyncSession session, string rootName)
        {
            // Including MongoCollection in the cleanup
            await session.RunAsync(
                @"MATCH (n) 
                  WHERE n.project = $project 
                  AND (n:Class OR n:Method OR n:Interface OR n:ExternalService OR n:MongoCollection) 
                  DETACH DELETE n",
                new { project = rootName });

            // Remove the root node if it exists
            await session.RunAsync("MATCH (r:Root {project: $project}) DETACH DELETE r", new { project = rootName });

            Console.WriteLine("Cleaned up data from previous run");
        }

        static bool IsUsefulLink(string sourceType, string targetType, string relationshipType)
        {
            return true;
        }

        static (string MethodId, string ClassId, string MethodName)? ResolveMethod(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
        {
            if (semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol methodSymbol)
            {
                string methodName = methodSymbol.Name;
                string className = methodSymbol.ContainingType?.Name ?? "Unknown";
                string namespaceName = methodSymbol.ContainingType?.ContainingNamespace.ToDisplayString();
                string projectName = methodSymbol.ContainingAssembly?.Name ?? "UnknownProject";

                string classId = $"{projectName}:{namespaceName}.{className}";
                string methodSignature = $"{methodName}({string.Join(", ", methodSymbol.Parameters.Select(p => p.Type.Name))})";
                string methodId = $"{classId}.{methodSignature}";

                return (methodId, classId, methodName);
            }
            return null;
        }

        static bool IsHttpCall(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
        {
            var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol == null) return false;

            var containingType = methodSymbol.ContainingType;
            if (containingType == null) return false;

            // Check if it's a System.Net.Http.HttpClient or a type containing HttpClient in its name
            // More robust: Check namespace and name
            if (containingType.ToDisplayString().StartsWith("System.Net.Http.HttpClient"))
                return true;

            return containingType.Name.Contains("HttpClient", StringComparison.OrdinalIgnoreCase);
        }

        static string ExtractHttpServiceUrl(InvocationExpressionSyntax httpCall, SemanticModel semanticModel)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(httpCall);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            if (methodSymbol != null && methodSymbol.ContainingType.ToDisplayString().StartsWith("System.Net.Http.HttpClient"))
            {
                var arguments = httpCall.ArgumentList?.Arguments;
                if (arguments != null && arguments.Count > 0)
                {
                    var firstArgument = arguments[0].Expression;
                    if (firstArgument is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        return literal.Token.ValueText;
                    }
                    else if (semanticModel.GetConstantValue(firstArgument).HasValue)
                    {
                        return semanticModel.GetConstantValue(firstArgument).Value?.ToString() ?? "UnknownServiceUrl";
                    }
                }
            }

            return "UnknownServiceUrl";
        }

        static bool IsMongoDbQuery(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
        {
            var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol == null) return false;

            // Check if the containing type is from MongoDB.Driver namespace
            // This is more reliable than just checking the name.
            var ns = methodSymbol.ContainingType?.ContainingNamespace?.ToDisplayString();
            if (ns != null && ns.StartsWith("MongoDB.Driver", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        static string ExtractCollectionName(InvocationExpressionSyntax mongoCall, SemanticModel semanticModel)
        {
            // Attempt to find if this invocation is actually calling GetCollection<T>("collectionName")
            // If so, extract that collection name. Otherwise, fallback to a generic approach.

            var methodSymbol = semanticModel.GetSymbolInfo(mongoCall).Symbol as IMethodSymbol;
            if (methodSymbol == null) return "UnknownCollection";

            if (methodSymbol.Name.Equals("GetCollection", StringComparison.OrdinalIgnoreCase))
            {
                var arguments = mongoCall.ArgumentList?.Arguments;
                if (arguments != null && arguments.Count > 0)
                {
                    var firstArgument = arguments[0].Expression;
                    if (firstArgument is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        return literal.Token.ValueText;
                    }
                    else if (semanticModel.GetConstantValue(firstArgument).HasValue)
                    {
                        return semanticModel.GetConstantValue(firstArgument).Value?.ToString() ?? "UnknownCollection";
                    }
                }
            }

            // If it's not a GetCollection call, we try a fallback approach.
            // Many MongoDB operations (Find, InsertOne, etc.) happen on an IMongoCollection<T> instance.
            // Without deeper data-flow analysis, we can't reliably get the collection name here.
            // We'll just return "UnknownCollection".
            return "UnknownCollection";
        }

        static async Task ProcessPendingMongoLinks(IAsyncSession session)
        {
            int maxAttempts = 5;
            int delayMs = 1000;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (pendingMongoLinks.IsEmpty)
                {
                    break;
                }

                var currentLinks = new List<(string MethodId, string CollectionId)>();
                while (pendingMongoLinks.TryDequeue(out var link))
                {
                    currentLinks.Add(link);
                }

                foreach (var (methodId, collectionId) in currentLinks)
                {
                    if (string.IsNullOrWhiteSpace(methodId) || string.IsNullOrWhiteSpace(collectionId))
                    {
                        Console.WriteLine($"Invalid methodId or collectionId: {methodId}, {collectionId}");
                        continue;
                    }

                    try
                    {
                        // Merge MongoCollection node
                        var parts = collectionId.Split('.');
                        string database = parts.Length > 1 ? parts[1] : "UnknownDatabase";
                        string collName = parts.Length > 2 ? parts[2] : parts.Last();

                        await CreateNode(session, collectionId, "MongoCollection", new Dictionary<string, object>
                        {
                            { "name", collName },
                            { "database", database },
                            { "color", NodeColors["MongoCollection"] }
                        });

                        await CreateRelationship(session, methodId, collectionId, "QUERIES");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing MongoDB link: {ex.Message}");
                        pendingMongoLinks.Enqueue((methodId, collectionId));  // Re-enqueue if failed
                    }
                }

                if (!pendingMongoLinks.IsEmpty)
                {
                    Console.WriteLine($"Waiting {delayMs}ms before next attempt...");
                    await Task.Delay(delayMs);
                    delayMs *= 2; // Exponential backoff
                }
            }

            if (!pendingMongoLinks.IsEmpty)
            {
                Console.WriteLine($"Warning: {pendingMongoLinks.Count} MongoDB links could not be created after {maxAttempts} attempts.");
            }
        }

        static string CreateUrl(string filePath, int lineNumber)
        {
            string relativePath = filePath.Replace("/app/", "");
            return $"{BaseUrl}/{relativePath}#{lineNumber}";
        }

        static string ExtractProjectFromId(string classId)
        {
            // classId is in format: projectName:namespace.class
            // Extract everything before the first colon as project name
            var parts = classId.Split(':');
            return parts.Length > 0 ? parts[0] : "UnknownProject";
        }
    }
}
