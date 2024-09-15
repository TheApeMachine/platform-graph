<?php

require __DIR__ . '/../vendor/autoload.php';

use PhpParser\ParserFactory;
use PhpParser\NodeTraverser;
use PhpParser\NodeVisitorAbstract;
use PhpParser\Node;
use Laudis\Neo4j\ClientBuilder;

$rootName = getenv('ROOT_NAME') ?: 'UnknownRoot';
$baseUrl = getenv('BASE_URL') ?: 'http://localhost';
$projectRoot = '/app';

function createUrl($filePath, $lineNumber) {
    global $baseUrl, $projectRoot;
    $relativePath = str_replace($projectRoot, '', $filePath);
    return "{$baseUrl}{$relativePath}#{$lineNumber}";
}

// Connect to Neo4j
$client = ClientBuilder::create()
    ->withDriver('bolt', getenv('NEO4J_URI'), \Laudis\Neo4j\Authentication\Authenticate::basic(getenv('NEO4J_USER'), getenv('NEO4J_PASSWORD')))
    ->build();

// Clean up data from previous run
$client->run('MATCH (n) WHERE n.project = $project DETACH DELETE n', ['project' => $rootName]);
echo "Cleaned up data from previous run\n";

// Create uniqueness constraints
$client->run('CREATE CONSTRAINT IF NOT EXISTS FOR (c:Class) REQUIRE c.id IS UNIQUE');
$client->run('CREATE CONSTRAINT IF NOT EXISTS FOR (f:Function) REQUIRE f.id IS UNIQUE');
$client->run('CREATE CONSTRAINT IF NOT EXISTS FOR (m:Method) REQUIRE m.id IS UNIQUE');

// Create root node
$client->run('MERGE (r:Root {name: $name, project: $project, color: $color})', ['name' => $rootName, 'project' => $rootName, 'color' => 'orange']);

// Recursive function to parse directories
function parseDirectory($dir, $parser, $client)
{
    $files = scandir($dir);
    foreach ($files as $file) {
        if ($file !== '.' && $file !== '..') {
            $path = "$dir/$file";
            if (is_dir($path)) {
                parseDirectory($path, $parser, $client);
            } else {
                if (pathinfo($file, PATHINFO_EXTENSION) === 'php') {
                    parseFile($path, $parser, $client);
                }
            }
        }
    }
}

// Function to parse a PHP file
function parseFile($file, $parser, $client)
{
    global $rootName;
    $code = file_get_contents($file);
    try {
        $stmts = $parser->parse($code);

        $traverser = new NodeTraverser();
        $traverser->addVisitor(new class($client, $file, $rootName) extends NodeVisitorAbstract {
            private $client;
            private $file;
            private $rootName;
            private $currentNamespace;
            private $currentClass;

            public function __construct($client, $file, $rootName)
            {
                $this->client = $client;
                $this->file = $file;
                $this->rootName = $rootName;
            }

            public function enterNode(Node $node)
            {
                if ($node instanceof Node\Stmt\Namespace_) {
                    $this->currentNamespace = $node->name->toString();
                    $namespaceId = "{$this->rootName}:{$this->currentNamespace}";

                    // Create or merge Namespace node
                    $this->client->run(
                        'MERGE (ns:Namespace {id: $id}) ON CREATE SET ns.name = $name, ns.project = $project',
                        ['id' => $namespaceId, 'name' => $this->currentNamespace, 'project' => $this->rootName]
                    );

                    // Link Root to Namespace
                    $this->client->run(
                        'MATCH (r:Root {project: $project}), (ns:Namespace {id: $namespaceId}) MERGE (r)-[:CONTAINS]->(ns)',
                        ['project' => $this->rootName, 'namespaceId' => $namespaceId]
                    );
                }

                if ($node instanceof Node\Stmt\Class_) {
                    $this->currentClass = $node->name->toString();
                    $classId = "{$this->rootName}:{$this->currentNamespace}.{$this->currentClass}";
                    $url = createUrl($this->file, $node->getStartLine());

                    // Create or merge Class node
                    $this->client->run(
                        'MERGE (c:Class {id: $id}) ON CREATE SET c.name = $name, c.namespaceId = $namespaceId, c.project = $project, c.file = $file, c.url = $url',
                        ['id' => $classId, 'name' => $this->currentClass, 'namespaceId' => "{$this->rootName}:{$this->currentNamespace}", 'project' => $this->rootName, 'file' => $this->file, 'url' => $url]
                    );

                    // Link Namespace to Class
                    $this->client->run(
                        'MATCH (ns:Namespace {id: $namespaceId}), (c:Class {id: $classId}) MERGE (ns)-[:DECLARES]->(c)',
                        ['namespaceId' => "{$this->rootName}:{$this->currentNamespace}", 'classId' => $classId]
                    );
                }

                if ($node instanceof Node\Stmt\Function_) {
                    $functionName = $node->name->toString();
                    $functionId = "{$this->rootName}:{$this->currentNamespace}.{$functionName}";
                    $url = createUrl($this->file, $node->getStartLine());

                    // Create or merge Function node
                    $this->client->run(
                        'MERGE (f:Function {id: $id}) ON CREATE SET f.name = $name, f.namespaceId = $namespaceId, f.project = $project, f.file = $file, f.url = $url',
                        ['id' => $functionId, 'name' => $functionName, 'namespaceId' => "{$this->rootName}:{$this->currentNamespace}", 'project' => $this->rootName, 'file' => $this->file, 'url' => $url]
                    );

                    // Link Namespace to Function
                    $this->client->run(
                        'MATCH (ns:Namespace {id: $namespaceId}), (f:Function {id: $functionId}) MERGE (ns)-[:DECLARES]->(f)',
                        ['namespaceId' => "{$this->rootName}:{$this->currentNamespace}", 'functionId' => $functionId]
                    );
                }

                if ($node instanceof Node\Stmt\ClassMethod) {
                    $methodName = $node->name->toString();
                    $methodId = "{$this->rootName}:{$this->currentNamespace}.{$this->currentClass}.{$methodName}";
                    $url = createUrl($this->file, $node->getStartLine());

                    // Create or merge Method node
                    $this->client->run(
                        'MERGE (m:Method {id: $id}) ON CREATE SET m.name = $name, m.classId = $classId, m.project = $project, m.file = $file, m.url = $url',
                        ['id' => $methodId, 'name' => $methodName, 'classId' => "{$this->rootName}:{$this->currentNamespace}.{$this->currentClass}", 'project' => $this->rootName, 'file' => $this->file, 'url' => $url]
                    );

                    // Link Class to Method
                    $this->client->run(
                        'MATCH (c:Class {id: $classId}), (m:Method {id: $methodId}) MERGE (c)-[:DECLARES]->(m)',
                        ['classId' => "{$this->rootName}:{$this->currentNamespace}.{$this->currentClass}", 'methodId' => $methodId]
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
