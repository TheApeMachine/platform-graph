package main

import (
	"context"
	"log"

	"github.com/neo4j/neo4j-go-driver/v5/neo4j"
	"github.com/theapemachine/platform-graph/graphlang"
)

// Main function to demonstrate usage.
func main() {
	neo4jURI := "bolt://host.docker.internal:7687"
	neo4jUser := "neo4j"
	neo4jPassword := "securepassword"

	log.Printf("Connecting to Neo4j at %s with user %s and password %s\n", neo4jURI, neo4jUser, neo4jPassword)
	driver, err := neo4j.NewDriverWithContext(neo4jURI, neo4j.BasicAuth(neo4jUser, neo4jPassword, ""))
	if err != nil {
		log.Fatalf("Failed to create Neo4j driver: %v", err)
	}
	defer driver.Close(context.Background())

	session := driver.NewSession(context.Background(), neo4j.SessionConfig{AccessMode: neo4j.AccessModeWrite})
	defer session.Close(context.Background())

	dirPath := "/app" // Replace with the actual directory path

	parser := graphlang.NewTreeSitterParser(driver)
	parser.AnalyzeDirectory(dirPath)
}