const fs = require('fs');
const path = require('path');
const babelParser = require('@babel/parser');
const traverse = require('@babel/traverse').default;
const neo4j = require('neo4j-driver');

// Node color mappings
const NodeColors = {
  Class: '#4287f5',
  Function: '#42f54e',
  Method: '#42f54e',
  ReactComponent: '#f54242',
  ExternalService: '#f5f542',
  MongoCollection: '#f5a442',
};

// Create Neo4j driver with retry logic
async function createNeo4jDriver(uri, username, password) {
  const maxRetries = 100;
  let delay = 5000;

  for (let attempt = 0; attempt < maxRetries; attempt++) {
    try {
      const driver = neo4j.driver(uri, neo4j.auth.basic(username, password));
      const session = driver.session();
      await session.run('RETURN 1');
      await session.close();
      console.log('Connected to Neo4j');
      return driver;
    } catch (error) {
      console.log(`Attempt ${attempt + 1}: Neo4j connection failed, retrying in ${delay / 1000} seconds...`);
      await new Promise((resolve) => setTimeout(resolve, delay));
      delay *= 2;
    }
  }
  throw new Error('Failed to connect to Neo4j after several attempts');
}

// Create uniqueness constraints in Neo4j
async function createUniquenessConstraints(session) {
  await session.run('CREATE CONSTRAINT IF NOT EXISTS FOR (c:Class) REQUIRE c.id IS UNIQUE');
  await session.run('CREATE CONSTRAINT IF NOT EXISTS FOR (f:Function) REQUIRE f.id IS UNIQUE');
  await session.run('CREATE CONSTRAINT IF NOT EXISTS FOR (m:Method) REQUIRE m.id IS UNIQUE');
}

// Clean up data from previous run
async function cleanupPreviousRunData(session, rootName) {
  await session.run('MATCH (n) WHERE n.project = $project DETACH DELETE n', { project: rootName });
  console.log('Cleaned up data from previous run');
}

// Analyze a JavaScript/TypeScript file
async function analyzeFile(filePath, session, rootName, baseUrl) {
  const code = fs.readFileSync(filePath, 'utf-8');
  const projectRoot = '/app';

  function createUrl(filePath, lineNumber) {
    const relativePath = path.relative(projectRoot, filePath);
    return `${baseUrl}/${relativePath}#${lineNumber}`;
  }

  try {
    const ast = babelParser.parse(code, {
      sourceType: 'module',
      plugins: [
        'jsx',
        'typescript',
        'classProperties',
        'decorators-legacy',
        'exportDefaultFrom',
        'exportNamespaceFrom',
        'dynamicImport',
        'objectRestSpread',
        'optionalChaining',
        'nullishCoalescingOperator',
      ],
    });

    const processedNodes = new Set();

    const tx = session.beginTransaction(); // Start a new transaction

    traverse(ast, {
      enter(path) {
        if (path.node.loc) {
          path.node.loc.file = filePath;
        }
      },
      async ClassDeclaration(path) {
        const className = path.node.id.name;
        const classId = `${rootName}:${className}`;
        const url = createUrl(filePath, path.node.loc.start.line);

        if (!processedNodes.has(classId)) {
          try {
            await tx.run(
              'MERGE (c:Class {id: $id}) ON CREATE SET c.name = $name, c.project = $project, c.file = $file, c.color = $color, c.url = $url',
              {
                id: classId,
                name: className,
                project: rootName,
                file: filePath,
                color: NodeColors.Class,
                url: url,
              }
            );
            processedNodes.add(classId);
          } catch (error) {
            console.error(`Failed to create or merge class ${className}:`, error);
          }
        }
      },
      async FunctionDeclaration(path) {
        const functionName = path.node.id.name;
        const functionId = `${rootName}:${functionName}`;
        const url = createUrl(filePath, path.node.loc.start.line);

        if (!processedNodes.has(functionId)) {
          try {
            await tx.run(
              'MERGE (f:Function {id: $id}) ON CREATE SET f.name = $name, f.project = $project, f.file = $file, f.color = $color, f.url = $url',
              {
                id: functionId,
                name: functionName,
                project: rootName,
                file: filePath,
                color: NodeColors.Function,
                url: url,
              }
            );
            processedNodes.add(functionId);
          } catch (error) {
            console.error(`Failed to create or merge function ${functionName}:`, error);
          }
        }
      },
      async CallExpression(path) {
        const callee = path.node.callee;
        let calleeName = '';
        let calleeId = '';
        if (callee.type === 'Identifier') {
          calleeName = callee.name;
          calleeId = `${rootName}:${calleeName}`;
        } else if (callee.type === 'MemberExpression') {
          const objectName = callee.object.name || 'unknown';
          const propertyName = callee.property.name || 'unknown';
          calleeName = `${objectName}.${propertyName}`;
          calleeId = `${rootName}:${calleeName}`;
        }

        const caller = path.getFunctionParent();
        let callerId = '';
        if (caller) {
          if (caller.node.type === 'FunctionDeclaration') {
            callerId = `${rootName}:${caller.node.id.name}`;
          } else if (caller.node.type === 'ClassMethod') {
            const className = caller.parentPath.parent.node.id.name;
            callerId = `${rootName}:${className}.${caller.node.key.name}`;
          }
        }

        // Create or merge called function/method
        if (!processedNodes.has(calleeId)) {
          try {
            await tx.run(
              'MERGE (c {id: $id}) ON CREATE SET c.name = $name, c.project = $project',
              {
                id: calleeId,
                name: calleeName,
                project: rootName,
              }
            );
            processedNodes.add(calleeId);
          } catch (error) {
            console.error(`Failed to create or merge callee ${calleeName}:`, error);
          }
        }

        // Create CALLS relationship
        if (callerId) {
          try {
            await tx.run(
              'MATCH (caller {id: $callerId}), (callee {id: $calleeId}) MERGE (caller)-[:CALLS]->(callee)',
              {
                callerId: callerId,
                calleeId: calleeId,
              }
            );
          } catch (error) {
            console.error(`Failed to create CALLS relationship between ${callerId} and ${calleeId}:`, error);
          }
        }
      },
      async JSXElement(path) {
        const componentName = path.node.openingElement.name.name;
        const componentId = `${rootName}:${componentName}`;
        const url = createUrl(filePath, path.node.loc.start.line);

        if (!processedNodes.has(componentId)) {
          try {
            await tx.run(
              'MERGE (r:ReactComponent {id: $id}) ON CREATE SET r.name = $name, r.project = $project, r.file = $file, r.color = $color, r.url = $url',
              {
                id: componentId,
                name: componentName,
                project: rootName,
                file: filePath,
                color: NodeColors.ReactComponent,
                url: url,
              }
            );
            processedNodes.add(componentId);
          } catch (error) {
            console.error(`Failed to create or merge React component ${componentName}:`, error);
          }
        }
      },
    });

    await tx.commit(); // Commit the transaction once all operations are done
  } catch (error) {
    console.error(`Error analyzing file: ${filePath}`);
    console.error(error);
  }
}

// Analyze all files in a directory recursively
async function analyzeDirectory(dir, session, rootName, baseUrl) {
  const files = fs.readdirSync(dir);
  for (const file of files) {
    const filePath = path.join(dir, file);

    if (fs.lstatSync(filePath).isDirectory()) {
      if (file === 'node_modules' || file.startsWith('.')) {
        continue;
      }
      await analyzeDirectory(filePath, session, rootName, baseUrl);
    } else if (file.endsWith('.js') || file.endsWith('.jsx') || file.endsWith('.ts') || file.endsWith('.tsx')) {
      await analyzeFile(filePath, session, rootName, baseUrl);
    }
  }
}

async function main() {
  const rootName = process.env.ROOT_NAME || 'UnknownRoot';
  const baseUrl = process.env.BASE_URL || 'http://localhost';
  const projectRoot = '/app';

  // Connect to Neo4j
  const driver = await createNeo4jDriver('bolt://host.docker.internal:7687', 'neo4j', 'securepassword');
  const session = driver.session();

  // Clean up previous data and create constraints
  await cleanupPreviousRunData(session, rootName);
  await createUniquenessConstraints(session);

  // Create root node
  await session.run(
    'MERGE (r:Root {name: $name, project: $project, color: $color})',
    { name: rootName, project: rootName, color: 'orange' }
  );

  // Analyze the project directory
  await analyzeDirectory(projectRoot, session, rootName, baseUrl);

  // Close session and driver
  await session.close();
  await driver.close();
}

main().catch((error) => {
  console.error('Error:', error);
});
