import time
import logging
from neo4j import GraphDatabase
from pymongo import MongoClient
from bson import ObjectId
import os

# Set up logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

# Node colors
NODE_COLORS = {
    "Root": "orange",
    "Database": "#00BFFF",       # DeepSkyBlue
    "Collection": "#8A2BE2"      # BlueViolet
}

def create_neo4j_driver():
    uri = os.getenv("NEO4J_URI")
    user = os.getenv("NEO4J_USER")
    password = os.getenv("NEO4J_PASSWORD")

    if not uri or not user or not password:
        raise RuntimeError("NEO4J_URI, NEO4J_USER, and NEO4J_PASSWORD must be set")

    driver = GraphDatabase.driver(uri, auth=(user, password))
    max_retries = 100
    delay = 5
    for attempt in range(max_retries):
        try:
            with driver.session() as session:
                session.run("RETURN 1")
            logging.info("Connected to Neo4j")
            return driver
        except Exception as e:
            logging.warning(f"Attempt {attempt + 1}: Neo4j connection failed, retrying in {delay} seconds... Error: {str(e)}")
            time.sleep(delay)
            delay *= 1.5
    raise Exception("Failed to connect to Neo4j after several attempts")

def main():
    # MongoDB connection
    mongo_uri = os.getenv("MONGO_URI")
    if not mongo_uri:
        raise RuntimeError("MONGO_URI environment variable must be set for MongoDB connection.")

    mongo_client = MongoClient(mongo_uri)
    db_name = "FanApp"  # Replace with your database name if needed or make configurable
    db = mongo_client[db_name]

    neo4j_driver = create_neo4j_driver()

    with neo4j_driver.session() as session:
        # Create uniqueness constraints
        create_uniqueness_constraints(session)

        # Create or update root node
        root_name = os.getenv("ROOT_NAME", "UnknownRoot")
        root_id = f"root:{root_name}"
        session.run(
            "MERGE (r:Root {id:$id}) "
            "ON CREATE SET r.name=$name, r.project=$project, r.color=$color "
            "ON MATCH SET r.name=$name, r.project=$project, r.color=$color",
            {"id": root_id, "name": root_name, "project": root_name, "color": NODE_COLORS["Root"]}
        )

        # Create or update Database node and link it to Root
        database_id = f"mongo:{db.name}"
        session.run(
            "MERGE (d:Database {id:$id}) "
            "ON CREATE SET d.name=$name, d.project=$project, d.color=$color "
            "ON MATCH SET d.name=$name, d.project=$project, d.color=$color",
            {"id": database_id, "name": db.name, "project": root_name, "color": NODE_COLORS["Database"]}
        )
        # Link Root to Database
        session.run(
            "MATCH (r:Root {id:$rootId}), (d:Database {id:$databaseId}) MERGE (r)-[:CONTAINS]->(d)",
            {"rootId": root_id, "databaseId": database_id}
        )

        # Clean up previous run data (only Collections, since we want to preserve Root and Database)
        logging.info("Cleaning up collection data from previous run")
        session.run(
            "MATCH (c:Collection) DETACH DELETE c"
        )
        logging.info("Cleanup completed")

        # Extract schema and push to Neo4j
        extract_schema_and_push_to_neo4j(session, db, database_id)

        # Detect and create relationships between collections
        detect_and_create_relationships(session, db)

    logging.info("MongoDB analysis complete")

def create_uniqueness_constraints(session):
    session.run("CREATE CONSTRAINT IF NOT EXISTS FOR (r:Root) REQUIRE r.id IS UNIQUE")
    session.run("CREATE CONSTRAINT IF NOT EXISTS FOR (d:Database) REQUIRE d.id IS UNIQUE")
    session.run("CREATE CONSTRAINT IF NOT EXISTS FOR (c:Collection) REQUIRE c.id IS UNIQUE")

def extract_schema_and_push_to_neo4j(session, db, database_id, sample_size=100):
    """
    Extracts the schema of collections and pushes them to Neo4j.
    :param session: Neo4j session object.
    :param db: MongoDB database object.
    :param database_id: The id of the Database node in Neo4j.
    :param sample_size: Number of documents to sample for schema extraction.
    """
    for collection_name in db.list_collection_names():
        collection = db[collection_name]

        try:
            schema = get_collection_schema(collection, sample_size)
            collection_id = f"mongo:{db.name}.{collection_name}"

            # Create or merge Collection node
            session.run(
                "MERGE (c:Collection {id: $id}) "
                "ON CREATE SET c.name = $name, c.database = $database, c.color = $color, c.schema = $schema "
                "ON MATCH SET c.name = $name, c.database = $database, c.color = $color, c.schema = $schema",
                {
                    "id": collection_id,
                    "name": collection_name,
                    "database": db.name,
                    "color": NODE_COLORS["Collection"],
                    "schema": str(schema)
                }
            )

            # Link Database to Collection
            session.run(
                "MATCH (d:Database {id:$databaseId}), (c:Collection {id:$collectionId}) "
                "MERGE (d)-[:DECLARES]->(c)",
                {"databaseId": database_id, "collectionId": collection_id}
            )

            logging.info(f"Processed collection: {collection_name}")
        except Exception as e:
            logging.error(f"Failed to process collection {collection_name}: {e}")

def get_collection_schema(collection, sample_size=100):
    """
    Generates the schema for a given MongoDB collection.
    :param collection: MongoDB collection object.
    :param sample_size: Number of documents to sample for schema extraction.
    :return: Schema dictionary with field types.
    """
    schema = {}
    try:
        for doc in collection.find().limit(sample_size):
            for key, value in doc.items():
                if key not in schema:
                    schema[key] = set()
                schema[key].add(type(value).__name__)

        # Convert sets to lists for serialization
        return {k: list(v) for k, v in schema.items()}
    except Exception as e:
        logging.error(f"Error extracting schema for collection {collection.name}: {e}")
        return schema

def detect_and_create_relationships(session, db, sample_size=100):
    """
    Detects relationships between collections based on field naming conventions and ObjectId references.
    :param session: Neo4j session object.
    :param db: MongoDB database object.
    :param sample_size: Number of documents to sample for relationship detection.
    """
    collection_names = db.list_collection_names()
    field_to_collection = {}

    # Build a mapping of fields to collections based on naming convention "<collectionName>Id"
    for collection_name in collection_names:
        field_to_collection[f"{collection_name}Id"] = collection_name

    for collection_name in collection_names:
        collection = db[collection_name]
        collection_id = f"mongo:{db.name}.{collection_name}"
        foreign_keys = {}

        try:
            # Sample documents to detect foreign keys
            for doc in collection.find().limit(sample_size):
                for field, value in doc.items():
                    # Check if field name matches <otherCollection>Id and value is an ObjectId or list of ObjectIds
                    if field in field_to_collection:
                        # One-to-One / One-to-Many relationship
                        if isinstance(value, ObjectId):
                            referenced_collection = field_to_collection[field]
                            foreign_keys[field] = referenced_collection
                        elif isinstance(value, list) and all(isinstance(v, ObjectId) for v in value):
                            referenced_collection = field_to_collection[field]
                            foreign_keys[field] = referenced_collection

            # Create relationships in Neo4j
            for field, referenced_collection in foreign_keys.items():
                referenced_collection_id = f"mongo:{db.name}.{referenced_collection}"
                session.run(
                    "MATCH (c1:Collection {id: $collectionId}), (c2:Collection {id: $referencedCollectionId}) "
                    "MERGE (c1)-[:REFERENCES {field: $field}]->(c2)",
                    {
                        "collectionId": collection_id,
                        "referencedCollectionId": referenced_collection_id,
                        "field": field
                    }
                )
                logging.info(f"Created relationship: {collection_name} -> {referenced_collection} via field {field}")
        except Exception as e:
            logging.error(f"Error processing relationships for collection {collection_name}: {e}")

if __name__ == "__main__":
    main()
