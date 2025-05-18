package main

import (
	"context"
	"log"
	"os"

	"github.com/neo4j/neo4j-go-driver/v5/neo4j"
	"github.com/theapemachine/platform-graph/graphlang"
)

// main initializes configuration from environment variables, connects to a Neo4j database, and analyzes a directory using a TreeSitter-based parser.
func main() {
	neo4jURI := os.Getenv("NEO4J_URI")
	neo4jUser := os.Getenv("NEO4J_USER")
	neo4jPassword := os.Getenv("NEO4J_PASSWORD")
	rootName := os.Getenv("ROOT_NAME")
	baseURL := os.Getenv("BASE_URL")
	if neo4jURI == "" || neo4jUser == "" || neo4jPassword == "" {
		log.Fatal("NEO4J_URI, NEO4J_USER and NEO4J_PASSWORD must be set")
	}
	if rootName == "" {
		rootName = "UnknownRoot"
	}
	if baseURL == "" {
		baseURL = "http://localhost"
	}

	log.Printf("Connecting to Neo4j at %s with user %s and password %s\n", neo4jURI, neo4jUser, neo4jPassword)
	driver, err := neo4j.NewDriverWithContext(neo4jURI, neo4j.BasicAuth(neo4jUser, neo4jPassword, ""))
	if err != nil {
		log.Fatalf("Failed to create Neo4j driver: %v", err)
	}
	defer driver.Close(context.Background())

	dirPath := "/app"

	parser := graphlang.NewTreeSitterParser(driver, rootName, baseURL, dirPath)
	parser.AnalyzeDirectory(dirPath)
}
