# 🌐 Platform Graph

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Neo4j](https://img.shields.io/badge/Neo4j-4.4-blue)](https://neo4j.com/)

## 📊 Overview

Platform Graph is a powerful tool that scans codebases and builds a holistic graph view using Neo4j. It captures objects, methods, collections, and other relevant elements across multiple programming languages, providing a comprehensive visualization of your project's structure.

It scans codebases and builds a holistic graph view, using Neo4j, of all the objects, methods, collections, and other relevant elements in the codebase.

This will allow you to see how different parts of the codebase are connected and how they are used by each other, and with some Cypher magic, you can get pretty
advanced insights into the codebase.

## 🚀 Features

- **Multi-language Support**: Analyzes Go, Python, C#, PHP, and JavaScript/TypeScript codebases.
- **Graph Visualization**: Builds an interactive Neo4j graph representation of your codebase.
- **Relationship Mapping**: Shows how different parts of the codebase are connected and used.
- **Advanced Insights**: Leverage Cypher queries to gain deep insights into your codebase structure.
- **Docker Integration**: Easy setup and configuration using Docker Compose.

## 🛠️ Technologies Used

- **Neo4j**: Graph database for storing and querying codebase structure.
- **Docker**: For containerization and easy deployment.
- **Go, Python, C#, PHP, JavaScript/TypeScript**: Supported languages for codebase analysis.

## 🏗️ Project Structure

The project consists of multiple components for different language analyses, all orchestrated through Docker Compose.

## 🚀 Getting Started

### Prerequisites

- Docker and Docker Compose
- Neo4j (v4.4 or later)

### Installation and Setup

1. Clone the repository:
   ```bash
   git clone https://github.com/yourusername/platform-graph.git
   cd platform-graph
   ```

2. Create an `.env` file based on `.env.example`:
   ```bash
   cp .env.example .env
   ```
   Edit the `.env` file to set your Neo4j credentials and other configuration options.

3. Update the `docker-compose.yml` file to point to your projects:
   ```yaml
   volumes:
     - /path/to/your/project:/app
   ```

4. Run the analyzers using Docker Compose:
   ```bash
   docker-compose up
   ```

## 🔍 Usage

1. After running the analyzers, access the Neo4j interface:
   The Neo4j instance is exposed at http://localhost:7474

2. Alternatively, use the [yWorks Neo4j Explorer](https://www.yworks.com/neo4j-explorer/) for an enhanced interactive graph experience.

3. Use Cypher queries to explore your codebase structure across different languages.

### 🎯 Example Queries

Here are some example Cypher queries to get you started, categorized by difficulty:

#### 🟢 Beginner Queries

1. Find the top 10 classes with the most methods:
   ```cypher
   MATCH (c:Class)-[:HAS_METHOD]->(m:Method)
   WITH c, count(m) AS methodCount
   RETURN c.name AS ClassName, methodCount
   ORDER BY methodCount DESC
   LIMIT 10
   ```

2. List all unique file types in the project:
   ```cypher
   MATCH (f:File)
   RETURN DISTINCT f.extension AS FileType, count(*) AS Count
   ORDER BY Count DESC
   LIMIT 20
   ```

3. Find the top 5 longest method names:
   ```cypher
   MATCH (m:Method)
   RETURN m.name AS MethodName, size(m.name) AS NameLength
   ORDER BY NameLength DESC
   LIMIT 5
   ```

4. Count the number of classes, methods, and files:
   ```cypher
   MATCH (c:Class)
   WITH count(c) AS classCount
   MATCH (m:Method)
   WITH classCount, count(m) AS methodCount
   MATCH (f:File)
   RETURN classCount, methodCount, count(f) AS fileCount
   ```

5. Find the top 10 most common words in method names:
   ```cypher
   MATCH (m:Method)
   UNWIND split(toLower(m.name), "(?<=.)(?=[A-Z])|[^a-zA-Z]") AS word
   WITH word WHERE size(word) > 2
   RETURN word, count(*) AS frequency
   ORDER BY frequency DESC
   LIMIT 10
   ```

#### 🟠 Intermediate Queries

1. Find classes with high fan-out (many dependencies):
   ```cypher
   MATCH (c:Class)-[:DEPENDS_ON]->(dep:Class)
   WITH c, count(DISTINCT dep) AS dependencies
   WHERE dependencies > 5
   RETURN c.name AS ClassName, dependencies
   ORDER BY dependencies DESC
   LIMIT 10
   ```

2. Identify potential god classes (classes with many methods and attributes):
   ```cypher
   MATCH (c:Class)
   OPTIONAL MATCH (c)-[:HAS_METHOD]->(m:Method)
   OPTIONAL MATCH (c)-[:HAS_ATTRIBUTE]->(a:Attribute)
   WITH c, count(DISTINCT m) AS methodCount, count(DISTINCT a) AS attrCount
   WHERE methodCount + attrCount > 20
   RETURN c.name AS ClassName, methodCount, attrCount, methodCount + attrCount AS Complexity
   ORDER BY Complexity DESC
   LIMIT 10
   ```

3. Find circular dependencies between classes:
   ```cypher
   MATCH (c1:Class)-[:DEPENDS_ON*1..3]->(c2:Class)-[:DEPENDS_ON]->(c1)
   RETURN c1.name AS Class1, c2.name AS Class2
   LIMIT 20
   ```

4. Identify methods with high cyclomatic complexity:
   ```cypher
   MATCH (m:Method)
   WHERE m.cyclomaticComplexity > 10
   RETURN m.name AS MethodName, m.class AS ClassName, m.cyclomaticComplexity AS Complexity
   ORDER BY Complexity DESC
   LIMIT 15
   ```

5. Find classes that might violate the Single Responsibility Principle:
   ```cypher
   MATCH (c:Class)-[:HAS_METHOD]->(m:Method)
   WITH c, count(DISTINCT m) AS methodCount, collect(DISTINCT m.name) AS methodNames
   WHERE methodCount > 10
   AND size([name IN methodNames WHERE name CONTAINS 'get' OR name CONTAINS 'set']) < methodCount * 0.5
   RETURN c.name AS ClassName, methodCount, methodNames
   LIMIT 10
   ```

#### 🔴 Advanced Queries

1. Analyze the depth of inheritance trees:
   ```cypher
   MATCH path = (c:Class)-[:INHERITS_FROM*]->(base:Class)
   WHERE NOT (base)-[:INHERITS_FROM]->()
   WITH c, base, length(path) AS depth
   ORDER BY depth DESC
   RETURN base.name AS BaseClass, collect({class: c.name, depth: depth}) AS InheritanceChain
   LIMIT 10
   ```

2. Identify potential feature envy (methods that use more external class members):
   ```cypher
   MATCH (m:Method)-[:CALLS]->(extMethod:Method)
   WHERE m.class <> extMethod.class
   WITH m, count(DISTINCT extMethod) AS externalCalls
   MATCH (m)-[:CALLS]->(intMethod:Method)
   WHERE m.class = intMethod.class
   WITH m, externalCalls, count(DISTINCT intMethod) AS internalCalls
   WHERE externalCalls > internalCalls * 2
   RETURN m.name AS MethodName, m.class AS ClassName, externalCalls, internalCalls
   ORDER BY externalCalls DESC
   LIMIT 15
   ```

3. Find the most central classes in the dependency graph:
   ```cypher
   MATCH (c:Class)
   OPTIONAL MATCH (c)-[:DEPENDS_ON]->(dep:Class)
   OPTIONAL MATCH (other:Class)-[:DEPENDS_ON]->(c)
   WITH c, count(DISTINCT dep) AS outDegree, count(DISTINCT other) AS inDegree
   RETURN c.name AS ClassName, outDegree, inDegree, outDegree + inDegree AS Centrality
   ORDER BY Centrality DESC
   LIMIT 20
   ```

4. Detect potential design patterns (e.g., Singleton):
   ```cypher
   MATCH (c:Class)-[:HAS_METHOD]->(m:Method)
   WHERE m.name CONTAINS 'getInstance' AND m.isStatic = true
   WITH c, count(m) AS instanceMethods
   MATCH (c)-[:HAS_ATTRIBUTE]->(a:Attribute)
   WHERE a.isStatic = true AND a.type = c.name
   WITH c, instanceMethods, count(a) AS staticInstances
   WHERE instanceMethods > 0 AND staticInstances > 0
   RETURN c.name AS PotentialSingleton
   LIMIT 10
   ```

5. Analyze method call chains for potential refactoring:
   ```cypher
   MATCH path = (m1:Method)-[:CALLS*3..5]->(m2:Method)
   WHERE m1 <> m2
   WITH m1, m2, [node IN nodes(path) | node.name] AS callChain
   RETURN m1.name AS StartMethod, m2.name AS EndMethod, callChain, length(path) AS ChainLength
   ORDER BY ChainLength DESC
   LIMIT 20
   ```

These queries provide a range of analyses from basic structure exploration to advanced design pattern detection. Remember to adjust the LIMIT clauses based on your codebase size and performance requirements.

## 🌟 Advanced Features

- **Cross-language Analysis**: Gain insights from Go, Python, C#, PHP, and JavaScript/TypeScript codebases in a single graph.
- **Custom Queries**: Write specialized Cypher queries for unique insights.
- **Interactive Visualization**: Explore your codebase structure visually using Neo4j Browser or yWorks Neo4j Explorer.

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- Neo4j community for the powerful graph database.
- [yWorks](https://www.yworks.com/neo4j-explorer/) for their excellent Neo4j Explorer tool.
- All contributors and library authors whose work made this project possible.

---

📊 Happy Code Graphing! 🚀