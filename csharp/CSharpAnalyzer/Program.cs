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
            { "Method", "#42f54e" },
            { "HttpCall", "#f54242" },
            { "MongoCollection", "#f5a442" },
            { "Interface", "#f542f5" },
            { "ExternalService", "#f5f542" }
        };

        // Queue to handle pending MongoDB links
        private static ConcurrentQueue<(string MethodId, string CollectionId)> pendingMongoLinks = new();

        // **Added:** Class-level variable for baseUrl
        private static string BaseUrl;

        static async Task Main(string[] args)
        {
            // **Modified:** Initialize BaseUrl at class level
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
            await session.RunAsync(
                "MERGE (r:Root {name: $name, project: $project, color: $color})",
                new { name = rootName, project = rootName, color = "orange" });

            // Register MSBuild
            MSBuildLocator.RegisterDefaults();
            var workspace = MSBuildWorkspace.Create();

            // Open solution
            var solution = await workspace.OpenSolutionAsync(solutionPath);

            // Process each project in the solution
            foreach (var project in solution.Projects)
            {
                await ProcessProject(project, session);
            }

            // Process pending MongoDB links
            await ProcessPendingMongoLinks(session);

            await session.CloseAsync();
        }

        static async Task ProcessProject(Project project, IAsyncSession session)
        {
            string projectName = project.Name;

            foreach (var document in project.Documents)
            {
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue; // Null check for syntax tree

                var semanticModel = await document.GetSemanticModelAsync(); // Cache semantic model
                var root = await syntaxTree.GetRootAsync();

                var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
                foreach (var classDeclaration in classDeclarations)
                {
                    await ProcessClass(classDeclaration, semanticModel, session, projectName, document);
                }
            }
        }

        static async Task ProcessClass(ClassDeclarationSyntax classDeclaration, SemanticModel semanticModel, IAsyncSession session, string projectName, Document document)
        {
            string className = classDeclaration.Identifier.Text;
            string namespaceName = GetFullNamespace(classDeclaration);
            string classId = $"{projectName}:{namespaceName}.{className}";

            // Create or merge Class node
            await CreateNode(session, classId, "Class", new Dictionary<string, object>
            {
                { "name", className },
                { "namespace", namespaceName },
                { "project", projectName },
                { "file", document.FilePath },
                { "color", NodeColors["Class"] },
                { "url", CreateUrl(document.FilePath, classDeclaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1) }
            });

            // Process base classes and interfaces
            if (classDeclaration.BaseList != null)
            {
                foreach (var baseType in classDeclaration.BaseList.Types)
                {
                    await ProcessBaseType(baseType, session, projectName, namespaceName, classId);
                }
            }

            // Process method declarations
            var methodDeclarations = classDeclaration.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methodDeclarations)
            {
                await ProcessMethod(method, semanticModel, session, projectName, document, classId);
            }
        }

        static async Task ProcessBaseType(BaseTypeSyntax baseType, IAsyncSession session, string projectName, string namespaceName, string classId)
        {
            string baseTypeName = baseType.Type.ToString();
            string baseTypeId = $"{projectName}:{namespaceName}.{baseTypeName}";

            // Assuming base types can be classes or interfaces
            await CreateNode(session, baseTypeId, "Class", new Dictionary<string, object>
            {
                { "name", baseTypeName },
                { "project", projectName },
                { "color", NodeColors["Class"] }
            });

            // Create INHERITS_FROM relationship
            await CreateRelationship(session, classId, baseTypeId, "INHERITS_FROM");
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

            // Process method body if not null
            if (method.Body != null)
            {
                // Process method invocations
                var methodInvocations = method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var invocation in methodInvocations)
                {
                    var resolvedMethod = ResolveMethod(invocation, semanticModel);
                    if (resolvedMethod != null && IsUsefulLink("Method", "Method", "CALLS"))
                    {
                        await CreateNode(session, resolvedMethod.Value.MethodId, "Method", new Dictionary<string, object>
                        {
                            { "name", resolvedMethod.Value.MethodName },
                            { "classId", resolvedMethod.Value.ClassId },
                            { "project", projectName }
                        });

                        await CreateRelationship(session, methodId, resolvedMethod.Value.MethodId, "CALLS");
                    }
                }

                // Process HTTP calls
                var httpInvocations = method.Body.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(invocation => IsHttpCall(semanticModel, invocation));

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
                var mongoQueries = method.Body.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(invocation => IsMongoDbQuery(semanticModel, invocation));

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

            var query = $"MERGE (n:{label} {{id: $id}}) ON CREATE SET {string.Join(", ", properties.Select(p => $"n.{p.Key} = ${p.Key}"))}";
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
            await session.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (m:Method) REQUIRE m.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (col:MongoCollection) REQUIRE col.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT IF NOT EXISTS FOR (s:ExternalService) REQUIRE s.id IS UNIQUE");
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
            await session.RunAsync(
                "MATCH (n) WHERE n.project = $project AND (n:Class OR n:Method OR n:Interface OR n:ExternalService) DETACH DELETE n",
                new { project = rootName });
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
            return containingType != null && (
                containingType.Name.Contains("HttpClient") ||
                containingType.AllInterfaces.Any(i => i.Name.Contains("IHttpClient"))
            );
        }

        static string ExtractHttpServiceUrl(InvocationExpressionSyntax httpCall, SemanticModel semanticModel)
        {
            // Attempt to get the symbol info for the method being called
            var symbolInfo = semanticModel.GetSymbolInfo(httpCall);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            if (methodSymbol != null)
            {
                // Check if the method is part of HttpClient
                if (methodSymbol.ContainingType.Name.Contains("HttpClient"))
                {
                    // Extract the arguments passed to the method
                    var arguments = httpCall.ArgumentList.Arguments;

                    if (arguments.Count > 0)
                    {
                        // Get the first argument (usually the URL in methods like GetAsync, PostAsync, etc.)
                        var firstArgument = arguments[0].Expression;

                        // Check if the argument is a string literal
                        if (firstArgument is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            // Return the string value of the literal expression
                            return literal.Token.ValueText;
                        }
                        // Handle cases where the URL is a constant or a variable
                        else if (semanticModel.GetConstantValue(firstArgument).HasValue)
                        {
                            return semanticModel.GetConstantValue(firstArgument).Value?.ToString() ?? "UnknownServiceUrl";
                        }
                    }
                }
            }

            return "UnknownServiceUrl";
        }


        static bool IsMongoDbQuery(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
        {
            var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol == null) return false;

            var containingType = methodSymbol.ContainingType;
            return containingType != null && (
                containingType.Name.Contains("Mongo") ||
                containingType.AllInterfaces.Any(i => i.Name.Contains("IMongo"))
            );
        }

        static string ExtractCollectionName(InvocationExpressionSyntax mongoCall, SemanticModel semanticModel)
        {
            // Get the method symbol of the invocation
            var symbolInfo = semanticModel.GetSymbolInfo(mongoCall);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            if (methodSymbol != null)
            {
                // Check if the method belongs to a MongoDB-related type
                if (methodSymbol.ContainingType.Name.Contains("IMongoCollection"))
                {
                    // MongoDB collection-related methods usually chain from GetCollection
                    // Find the first argument in the method call, which could be the collection name
                    var arguments = mongoCall.ArgumentList.Arguments;

                    if (arguments.Count > 0)
                    {
                        // Assume the first argument might be the collection name (could be for `Find`, `InsertOne`, etc.)
                        var firstArgument = arguments[0].Expression;

                        // Check if it's a string literal
                        if (firstArgument is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            return literal.Token.ValueText;
                        }
                        // Handle cases where the collection name is a constant or a variable
                        else if (semanticModel.GetConstantValue(firstArgument).HasValue)
                        {
                            return semanticModel.GetConstantValue(firstArgument).Value?.ToString() ?? "UnknownCollection";
                        }
                    }
                }
            }

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
                        await CreateNode(session, collectionId, "MongoCollection", new Dictionary<string, object>
                        {
                            { "name", collectionId.Split('.').Last() },
                            { "database", collectionId.Split('.').Skip(1).FirstOrDefault() ?? "UnknownDatabase" },
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
    }
}
