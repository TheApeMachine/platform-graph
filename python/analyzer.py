import ast
import os
import time
import neo4j
from neo4j import GraphDatabase

# Node color mappings
NODE_COLORS = {
    'Class': '#4287f5',
    'Function': '#42f54e',
    'Method': '#42f54e',
    'ExternalService': '#f5f542',
    'MongoCollection': '#f5a442',
}

def create_neo4j_driver(uri, username, password):
    driver = GraphDatabase.driver(
        os.getenv("NEO4J_URI"),
        auth=(os.getenv("NEO4J_USER"), os.getenv("NEO4J_PASSWORD"))
    )
    max_retries = 100
    delay = 5
    for attempt in range(max_retries):
        try:
            with driver.session() as session:
                session.run("RETURN 1")
            print("Connected to Neo4j")
            return driver
        except Exception as e:
            print(f"Attempt {attempt + 1}: Neo4j connection failed, retrying in {delay} seconds...")
            time.sleep(delay)
            delay *= 2
    raise Exception("Failed to connect to Neo4j after several attempts")

def cleanup_previous_run_data(session, root_name):
    session.run("MATCH (n) WHERE n.project = $project DETACH DELETE n", {"project": root_name})
    print("Cleaned up data from previous run")

def create_uniqueness_constraints(session):
    session.run("CREATE CONSTRAINT IF NOT EXISTS FOR (c:Class) REQUIRE c.id IS UNIQUE")
    session.run("CREATE CONSTRAINT IF NOT EXISTS FOR (f:Function) REQUIRE f.id IS UNIQUE")
    session.run("CREATE CONSTRAINT IF NOT EXISTS FOR (m:Method) REQUIRE m.id IS UNIQUE")

def analyze_file(file_path, session, root_name, base_url):
    with open(file_path, 'r') as file:
        code = file.read()
    project_root = '/app'

    def create_url(file_path, line_number):
        relative_path = os.path.relpath(file_path, project_root)
        return f"{base_url}/{relative_path}#{line_number}"

    try:
        tree = ast.parse(code, filename=file_path)
        AnalyzerVisitor(session, root_name, file_path, create_url).visit(tree)
    except Exception as e:
        print(f"Error parsing {file_path}: {e}")

class AnalyzerVisitor(ast.NodeVisitor):
    def __init__(self, session, root_name, file_path, create_url):
        self.session = session
        self.root_name = root_name
        self.file_path = file_path
        self.create_url = create_url
        self.current_class = None

    def visit_ClassDef(self, node):
        class_name = node.name
        class_id = f"{self.root_name}:{class_name}"
        url = self.create_url(self.file_path, node.lineno)

        # Create or merge Class node
        self.session.run(
            "MERGE (c:Class {id: $id}) ON CREATE SET c.name = $name, c.project = $project, c.file = $file, c.url = $url",
            {"id": class_id, "name": class_name, "project": self.root_name, "file": self.file_path, "url": url}
        )

        # Link to root
        self.session.run(
            "MATCH (r:Root {project: $project}), (c:Class {id: $classId}) MERGE (r)-[:DECLARES]->(c)",
            {"project": self.root_name, "classId": class_id}
        )

        # Process methods
        self.current_class = class_id
        self.generic_visit(node)
        self.current_class = None

    def visit_FunctionDef(self, node):
        function_name = node.name
        if self.current_class:
            function_id = f"{self.current_class}.{function_name}"
            # It's a method
            self.session.run(
                "MERGE (m:Method {id: $id}) ON CREATE SET m.name = $name, m.classId = $classId, m.project = $project, m.file = $file, m.url = $url",
                {"id": function_id, "name": function_name, "classId": self.current_class, "project": self.root_name, "file": self.file_path, "url": self.create_url(self.file_path, node.lineno)}
            )
            # Link Class to Method
            self.session.run(
                "MATCH (c:Class {id: $classId}), (m:Method {id: $methodId}) MERGE (c)-[:DECLARES]->(m)",
                {"classId": self.current_class, "methodId": function_id}
            )
        else:
            function_id = f"{self.root_name}:{function_name}"
            # It's a function
            self.session.run(
                "MERGE (f:Function {id: $id}) ON CREATE SET f.name = $name, f.project = $project, f.file = $file, f.url = $url",
                {"id": function_id, "name": function_name, "project": self.root_name, "file": self.file_path, "url": self.create_url(self.file_path, node.lineno)}
            )
            # Link to root
            self.session.run(
                "MATCH (r:Root {project: $project}), (f:Function {id: $functionId}) MERGE (r)-[:DECLARES]->(f)",
                {"project": self.root_name, "functionId": function_id}
            )

        # Process function body
        self.generic_visit(node)

def analyze_directory(dir_path, session, root_name, base_url):
    for root, dirs, files in os.walk(dir_path):
        for file in files:
            if file.endswith('.py') and not file.startswith('.'):
                file_path = os.path.join(root, file)
                analyze_file(file_path, session, root_name, base_url)

def main():
    root_name = os.getenv('ROOT_NAME') or 'UnknownRoot'
    base_url = os.getenv('BASE_URL') or 'http://localhost'
    project_root = '/app'

    driver = create_neo4j_driver(os.getenv("NEO4J_URI"), os.getenv("NEO4J_USER"), os.getenv("NEO4J_PASSWORD"))
    with driver.session() as session:
        # Clean up previous data and create constraints
        cleanup_previous_run_data(session, root_name)
        create_uniqueness_constraints(session)

        # Create root node
        session.run(
            "MERGE (r:Root {name: $name, project: $project, color: $color})",
            {"name": root_name, "project": root_name, "color": 'orange'}
        )

        # Analyze the project directory
        analyze_directory(project_root, session, root_name, base_url)

    driver.close()

if __name__ == "__main__":
    main()
