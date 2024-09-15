import time
import logging
from neo4j import GraphDatabase
from pymongo import MongoClient
from bson import ObjectId
import os

# Set up logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

# MongoDB connection
mongo_client = MongoClient(os.getenv("MONGO_URI"))
db = mongo_client["FanApp"]  # Replace with your database name

# Node colors
NODE_COLORS = {
    "Collection": "#8A2BE2",  # BlueViolet
}

def create_neo4j_driver(uri, auth, max_retries=100, initial_delay=5):
    driver = GraphDatabase.driver(
        os.getenv("NEO4J_URI"),
        auth=(os.getenv("NEO4J_USER"), os.getenv("NEO4J_PASSWORD"))
    )
    delay = initial_delay
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
    neo4j_driver = create_neo4j_driver("bolt://host.docker.internal:7687", auth=("neo4j", "securepassword"))

    with neo4j_driver.session() as session:
        # Create uniqueness constraint
        session.run("CREATE CONSTRAINT IF NOT EXISTS FOR (c:Collection) REQUIRE c.id IS UNIQUE")
        
        # Clean up previous run data
        logging.info("Cleaning up data from previous run")
        session.run("MATCH (c:Collection) DETACH DELETE c")
        logging.info("Cleanup completed")

        # Extract schema and push to Neo4j
        extract_schema_and_push_to_neo4j(session)

        # Detect and create relationships between collections
        detect_and_create_relationships(session)

    logging.info("MongoDB analysis complete")

def extract_schema_and_push_to_neo4j(session, sample_size=100):
    """
    Extracts the schema of collections and pushes them to Neo4j.
    :param session: Neo4j session object.
    :param sample_size: Number of documents to sample for schema extraction.
    """
    for collection_name in db.list_collection_names():
        collection = db[collection_name]

        try:
            # Get the schema for the collection
            schema = get_collection_schema(collection, sample_size)

            # Create or merge Collection node
            collectionId = f"mongo:{db.name}.{collection_name}"

            session.run(
                "MERGE (c:Collection {id: $id}) " +
                "ON CREATE SET c.name = $name, c.database = $database, c.color = $color, c.schema = $schema",
                {
                    "id": collectionId,
                    "name": collection_name,
                    "database": db.name,
                    "color": NODE_COLORS["Collection"],
                    "schema": str(schema)
                }
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
        for doc in collection.find().limit(sample_size):  # Sample documents
            for key, value in doc.items():
                if key not in schema:
                    schema[key] = set()
                schema[key].add(type(value).__name__)

        return {k: list(v) for k, v in schema.items()}
    except Exception as e:
        logging.error(f"Error extracting schema for collection {collection.name}: {e}")
        return schema

def detect_and_create_relationships(session, sample_size=100):
    """
    Detects relationships between collections based on field naming conventions and ObjectId references.
    :param session: Neo4j session object.
    :param sample_size: Number of documents to sample for relationship detection.
    """
    collection_names = db.list_collection_names()
    field_to_collection = {}

    # Build a mapping of fields to collections
    for collection_name in collection_names:
        field_to_collection[f"{collection_name}Id"] = collection_name

    for collection_name in collection_names:
        collection = db[collection_name]
        collectionId = f"mongo:{db.name}.{collection_name}"
        foreign_keys = {}

        try:
            for doc in collection.find().limit(sample_size):
                for field, value in doc.items():
                    # Detect one-to-one or one-to-many relationships via ObjectId
                    if field in field_to_collection and isinstance(value, ObjectId):
                        referenced_collection = field_to_collection[field]
                        foreign_keys[field] = referenced_collection

                    # Handle case where the field is a list of ObjectIds (one-to-many relationship)
                    elif field in field_to_collection and isinstance(value, list) and all(isinstance(v, ObjectId) for v in value):
                        referenced_collection = field_to_collection[field]
                        foreign_keys[field] = referenced_collection

            # Create relationships in Neo4j
            for field, referenced_collection in foreign_keys.items():
                referenced_collectionId = f"mongo:{db.name}.{referenced_collection}"
                session.run(
                    "MATCH (c1:Collection {id: $collectionId}), (c2:Collection {id: $referencedCollectionId}) " +
                    "MERGE (c1)-[:REFERENCES {field: $field}]->(c2)",
                    {
                        "collectionId": collectionId,
                        "referencedCollectionId": referenced_collectionId,
                        "field": field
                    }
                )
                logging.info(f"Created relationship: {collection_name} -> {referenced_collection} via field {field}")
        except Exception as e:
            logging.error(f"Error processing relationships for collection {collection_name}: {e}")

if __name__ == "__main__":
    main()
