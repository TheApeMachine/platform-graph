<?php

require __DIR__ . '/../vendor/autoload.php';

use PhpParser\ParserFactory;
use PhpParser\NodeTraverser;
use PhpParser\NodeVisitorAbstract;
use PhpParser\Node;
use Laudis\Neo4j\ClientBuilder;

// Node color mappings
$NodeColors = [
    'Root' => 'orange',
    'Namespace' => '#f5a442',
    'Class' => '#4287f5',
    'Function' => '#42f54e',
    'Method' => '#42f54e'
];

$rootName = getenv('ROOT_NAME') ?: 'UnknownRoot';
$baseUrl = getenv('BASE_URL') ?: 'http://localhost';
$projectRoot = '/app';

// Create URL helper function
function createUrl($filePath, $lineNumber)
{
    global $baseUrl, $projectRoot;
    $relativePath = str_replace($projectRoot, '', $filePath);
    if (!str_ends_with($baseUrl, '/')) {
        $baseUrl .= '/';
    }
    return "{$baseUrl}" . ltrim($relativePath, '/') . "#{$lineNumber}";
}

// Connect to Neo4j with retry logic
function createNeo4jClient()
{
    $neo4jUri = getenv('NEO4J_URI');
    $neo4jUser = getenv('NEO4J_USER');
    $neo4jPassword = getenv('NEO4J_PASSWORD');
    if (!$neo4jUri || !$neo4jUser || !$neo4jPassword) {
        throw new RuntimeException("NEO4J_URI, NEO4J_USER, and NEO4J_PASSWORD environment variables must be set.");
    }

    // Build client
    return ClientBuilder::create()
        ->withDriver('bolt', $neo4jUri, \Laudis\Neo4j\Authentication\Authenticate::basic($neo4jUser, $neo4jPassword))
        ->build();
}

$client = createNeo4jClient();

// Clean up data from previous run
$client->run('MATCH (n) WHERE n.project = $project DETACH DELETE n', ['project' => $rootName]);
echo "Cleaned up data from previous run\n";

// Create uniqueness constraints
$client->run('CREATE CONSTRAINT IF NOT EXISTS FOR (r:Root) REQUIRE r.id IS UNIQUE');
$client->run('CREATE CONSTRAINT IF NOT EXISTS FOR (ns:Namespace) REQUIRE ns.id IS UNIQUE');
$client->run('CREATE CONSTRAINT IF NOT EXISTS FOR (c:Class) REQUIRE c.id IS UNIQUE');
$client->run('CREATE CONSTRAINT IF NOT EXISTS FOR (f:Function) REQUIRE f.id IS UNIQUE');
$client->run('CREATE CONSTRAINT IF NOT EXISTS FOR (m:Method) REQUIRE m.id IS UNIQUE');

// Create root node with unique ID
$rootId = "root:{$rootName}";
$client->run(
    'MERGE (r:Root {id:$id}) 
     ON CREATE SET r.name = $name, r.project = $project, r.color = $color
     ON MATCH SET r.name = $name, r.project = $project, r.color = $color',
    ['id' => $rootId, 'name' => $rootName, 'project' => $rootName, 'color' => $NodeColors['Root']]
);

// Recursive function to parse directories
function parseDirectory($dir, $parser, $client)
{
    $files = scandir($dir);
    foreach ($files as $file) {
        if ($file === '.' || $file === '..') {
            continue;
        }
        $path = "$dir/$file";
        if (is_dir($path)) {
            // Skip common vendor directories to speed up processing if desired
            if ($file === 'vendor' || $file === '.git') {
                continue;
            }
            parseDirectory($path, $parser, $client);
        } else {
            if (pathinfo($file, PATHINFO_EXTENSION) === 'php') {
                parseFile($path, $parser, $client);
            }
        }
    }
}

// Function to parse a PHP file
function parseFile($file, $parser, $client)
{
    global $rootName, $NodeColors;
    $code = file_get_contents($file);
    try {
        $stmts = $parser->parse($code);

        $traverser = new NodeTraverser();
        $traverser->addVisitor(new class($client, $file, $rootName, $NodeColors) extends NodeVisitorAbstract {
            private $client;
            private $file;
            private $rootName;
            private $NodeColors;
            private $currentNamespace;
            private $currentClass;

            public function __construct($client, $file, $rootName, $NodeColors)
            {
                $this->client = $client;
                $this->file = $file;
                $this->rootName = $rootName;
                $this->NodeColors = $NodeColors;
            }

            public function enterNode(Node $node)
            {
                global $rootId;

                // Handle Namespace
                if ($node instanceof Node\Stmt\Namespace_) {
                    $this->currentNamespace = $node->name ? $node->name->toString() : '';
                    $namespaceId = "{$this->rootName}:{$this->currentNamespace}";

                    $this->client->run(
                        'MERGE (ns:Namespace {id: $id})
                         ON CREATE SET ns.name = $name, ns.project = $project, ns.color = $color
                         ON MATCH SET ns.name = $name, ns.project = $project, ns.color = $color',
                        [
                            'id' => $namespaceId,
                            'name' => $this->currentNamespace,
                            'project' => $this->rootName,
                            'color' => $this->NodeColors['Namespace']
                        ]
                    );

                    // Link Root to Namespace
                    $this->client->run(
                        'MATCH (r:Root {id:$rootId}), (ns:Namespace {id: $namespaceId}) MERGE (r)-[:CONTAINS]->(ns)',
                        ['rootId' => "root:{$this->rootName}", 'namespaceId' => $namespaceId]
                    );
                }

                // Handle Class
                if ($node instanceof Node\Stmt\Class_) {
                    if (!$this->currentNamespace) {
                        // Handle classes without namespace (global namespace)
                        $this->currentNamespace = '';
                    }
                    $this->currentClass = $node->name ? $node->name->toString() : 'AnonymousClass';
                    $classId = "{$this->rootName}:{$this->currentNamespace}.{$this->currentClass}";
                    $url = createUrl($this->file, $node->getStartLine());

                    $this->client->run(
                        'MERGE (c:Class {id: $id})
                         ON CREATE SET c.name = $name, c.namespaceId = $namespaceId, c.project = $project, c.file = $file, c.url = $url, c.color = $color
                         ON MATCH SET c.name = $name, c.namespaceId = $namespaceId, c.project = $project, c.file = $file, c.url = $url, c.color = $color',
                        [
                            'id' => $classId,
                            'name' => $this->currentClass,
                            'namespaceId' => "{$this->rootName}:{$this->currentNamespace}",
                            'project' => $this->rootName,
                            'file' => $this->file,
                            'url' => $url,
                            'color' => $this->NodeColors['Class']
                        ]
                    );

                    // Link Namespace to Class
                    $this->client->run(
                        'MATCH (ns:Namespace {id: $namespaceId}), (c:Class {id: $classId}) MERGE (ns)-[:DECLARES]->(c)',
                        [
                            'namespaceId' => "{$this->rootName}:{$this->currentNamespace}",
                            'classId' => $classId
                        ]
                    );
                }

                // Handle Function
                if ($node instanceof Node\Stmt\Function_) {
                    $functionName = $node->name->toString();
                    $functionId = "{$this->rootName}:{$this->currentNamespace}.{$functionName}";
                    $url = createUrl($this->file, $node->getStartLine());

                    $this->client->run(
                        'MERGE (f:Function {id: $id})
                         ON CREATE SET f.name = $name, f.namespaceId = $namespaceId, f.project = $project, f.file = $file, f.url = $url, f.color = $color
                         ON MATCH SET f.name = $name, f.namespaceId = $namespaceId, f.project = $project, f.file = $file, f.url = $url, f.color = $color',
                        [
                            'id' => $functionId,
                            'name' => $functionName,
                            'namespaceId' => "{$this->rootName}:{$this->currentNamespace}",
                            'project' => $this->rootName,
                            'file' => $this->file,
                            'url' => $url,
                            'color' => $this->NodeColors['Function']
                        ]
                    );

                    // Link Namespace to Function
                    $this->client->run(
                        'MATCH (ns:Namespace {id: $namespaceId}), (f:Function {id: $functionId}) MERGE (ns)-[:DECLARES]->(f)',
                        [
                            'namespaceId' => "{$this->rootName}:{$this->currentNamespace}",
                            'functionId' => $functionId
                        ]
                    );
                }

                // Handle ClassMethod
                if ($node instanceof Node\Stmt\ClassMethod) {
                    // Ensure currentNamespace and currentClass are defined
                    $methodName = $node->name->toString();
                    $methodId = "{$this->rootName}:{$this->currentNamespace}.{$this->currentClass}.{$methodName}";
                    $url = createUrl($this->file, $node->getStartLine());

                    $this->client->run(
                        'MERGE (m:Method {id: $id})
                         ON CREATE SET m.name = $name, m.classId = $classId, m.project = $project, m.file = $file, m.url = $url, m.color = $color
                         ON MATCH SET m.name = $name, m.classId = $classId, m.project = $project, m.file = $file, m.url = $url, m.color = $color',
                        [
                            'id' => $methodId,
                            'name' => $methodName,
                            'classId' => "{$this->rootName}:{$this->currentNamespace}.{$this->currentClass}",
                            'project' => $this->rootName,
                            'file' => $this->file,
                            'url' => $url,
                            'color' => $this->NodeColors['Method']
                        ]
                    );

                    // Link Class to Method
                    $this->client->run(
                        'MATCH (c:Class {id: $classId}), (m:Method {id: $methodId}) MERGE (c)-[:DECLARES]->(m)',
                        [
                            'classId' => "{$this->rootName}:{$this->currentNamespace}.{$this->currentClass}",
                            'methodId' => $methodId
                        ]
                    );
                }
            }
        });

        $traverser->traverse($stmts);
    } catch (\PhpParser\Error $e) {
        echo "Parse error in file {$file}: {$e->getMessage()}\n";
    }
}

$parser = (new ParserFactory)->create(ParserFactory::PREFER_PHP7);
parseDirectory($projectRoot, $parser, $client);
