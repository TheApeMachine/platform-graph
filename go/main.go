package main

import (
	"context"
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/neo4j/neo4j-go-driver/v5/neo4j"
)

// Node color mappings
var NodeColors = map[string]string{
	"Package":         "#4287f5",
	"Function":        "#42f54e",
	"Method":          "#42f54e",
	"Struct":          "#f54242",
	"Interface":       "#f5a442",
	"ExternalService": "#f5f542",
}

// Create Neo4j driver with retry logic using context and timeout
func createNeo4jDriver() (neo4j.DriverWithContext, error) {
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute*10)
	defer cancel()

	var driver neo4j.DriverWithContext
	var err error
	retryDelay := time.Second * 5

	neo4jUri := os.Getenv("NEO4J_URI")
	neo4jUser := os.Getenv("NEO4J_USER")
	neo4jPassword := os.Getenv("NEO4J_PASSWORD")

	if neo4jUri == "" || neo4jUser == "" || neo4jPassword == "" {
		return nil, fmt.Errorf("missing required environment variables: NEO4J_URI, NEO4J_USER, NEO4J_PASSWORD")
	}

	for {
		select {
		case <-ctx.Done():
			return nil, fmt.Errorf("failed to connect to Neo4j after retries: %v", ctx.Err())
		case <-time.After(retryDelay):
			driver, err = neo4j.NewDriverWithContext(
				neo4jUri,
				neo4j.BasicAuth(neo4jUser, neo4jPassword, ""),
			)
			if err == nil {
				session := driver.NewSession(ctx, neo4j.SessionConfig{})
				defer session.Close(ctx)
				_, err = session.Run(ctx, "RETURN 1", nil)
				if err == nil {
					fmt.Println("Connected to Neo4j")
					return driver, nil
				}
			}
			fmt.Printf("Retrying connection to Neo4j after %v...\n", retryDelay)
		}
	}
}

// Clean up data from previous run
func cleanupPreviousRunData(ctx context.Context, session neo4j.SessionWithContext, rootName string) error {
	_, err := session.Run(ctx, "MATCH (n) WHERE n.project = $project DETACH DELETE n", map[string]interface{}{"project": rootName})
	if err != nil {
		return fmt.Errorf("failed to clean up previous data: %v", err)
	}
	fmt.Println("Cleaned up data from previous run")
	return nil
}

// Create uniqueness constraints in Neo4j
func createUniquenessConstraints(ctx context.Context, session neo4j.SessionWithContext) error {
	constraints := []string{
		"CREATE CONSTRAINT IF NOT EXISTS FOR (p:Package) REQUIRE p.id IS UNIQUE",
		"CREATE CONSTRAINT IF NOT EXISTS FOR (s:Struct) REQUIRE s.id IS UNIQUE",
		"CREATE CONSTRAINT IF NOT EXISTS FOR (f:Function) REQUIRE f.id IS UNIQUE",
		"CREATE CONSTRAINT IF NOT EXISTS FOR (m:Method) REQUIRE m.id IS UNIQUE",
	}
	for _, constraint := range constraints {
		_, err := session.Run(ctx, constraint, nil)
		if err != nil {
			return fmt.Errorf("failed to create uniqueness constraint: %v", err)
		}
	}
	return nil
}

// Find Go files in the project concurrently
func findGoFiles(root string) ([]string, error) {
	var wg sync.WaitGroup
	filesChan := make(chan string)
	var files []string
	var walkErr error

	// Goroutine to collect files from the channel
	go func() {
		for file := range filesChan {
			files = append(files, file)
		}
	}()

	err := filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			walkErr = err
			return err
		}
		if !info.IsDir() && strings.HasSuffix(info.Name(), ".go") && !strings.HasSuffix(info.Name(), "_test.go") {
			wg.Add(1)
			filesChan <- path
			wg.Done()
		}
		return nil
	})

	if err != nil {
		return nil, err
	}

	wg.Wait()
	close(filesChan)

	return files, walkErr
}

// Helper function to create URL
func createUrl(baseUrl, filePath, projectRoot string, lineNumber int) string {
	relativePath := strings.TrimPrefix(filePath, projectRoot)
	return fmt.Sprintf("%s%s#%d", baseUrl, relativePath, lineNumber)
}

func main() {
	rootName := os.Getenv("ROOT_NAME")
	if rootName == "" {
		log.Fatal("ROOT_NAME environment variable is not set")
	}
	baseUrl := os.Getenv("BASE_URL")
	if baseUrl == "" {
		baseUrl = "http://localhost"
	}
	projectRoot := "/app"

	// Connect to Neo4j
	ctx := context.Background()
	driver, err := createNeo4jDriver()
	if err != nil {
		log.Fatalf("Failed to connect to Neo4j: %v", err)
	}
	defer driver.Close(ctx)

	session := driver.NewSession(ctx, neo4j.SessionConfig{AccessMode: neo4j.AccessModeWrite})
	defer session.Close(ctx)

	// Clean up previous data and create constraints
	err = cleanupPreviousRunData(ctx, session, rootName)
	if err != nil {
		log.Fatalf("Cleanup error: %v", err)
	}

	err = createUniquenessConstraints(ctx, session)
	if err != nil {
		log.Fatalf("Constraint creation error: %v", err)
	}

	// Create root node
	_, err = session.Run(ctx,
		"MERGE (r:Root {name: $rootName, project: $project, color: $color})",
		map[string]interface{}{
			"rootName": rootName,
			"project":  rootName,
			"color":    "orange",
		})
	if err != nil {
		log.Fatalf("Failed to create root node: %v", err)
	}

	// Find Go files
	goFiles, err := findGoFiles(projectRoot)
	if err != nil {
		log.Fatalf("Failed to find Go files: %v", err)
	}

	// Process each Go file concurrently
	fset := token.NewFileSet()
	var wg sync.WaitGroup
	for _, filePath := range goFiles {
		wg.Add(1)
		go func(filePath string) {
			defer wg.Done()
			processGoFile(ctx, filePath, fset, projectRoot, baseUrl, session, rootName)
		}(filePath)
	}
	wg.Wait()
}

// Process Go file and extract AST information
func processGoFile(ctx context.Context, filePath string, fset *token.FileSet, projectRoot, baseUrl string, session neo4j.SessionWithContext, rootName string) {
	relativePath := strings.TrimPrefix(filePath, projectRoot+"/")
	packageName := filepath.Dir(relativePath)
	packageId := fmt.Sprintf("%s:%s", rootName, packageName)

	// Parse the Go file
	node, err := parser.ParseFile(fset, filePath, nil, parser.ParseComments)
	if err != nil {
		log.Printf("Failed to parse Go file %s: %v", filePath, err)
		return
	}

	// Create or merge Package node
	_, err = session.Run(ctx,
		"MERGE (p:Package {id: $id}) "+
			"ON CREATE SET p.name = $name, p.project = $project, p.color = $color, p.url = $url",
		map[string]interface{}{
			"id":      packageId,
			"name":    packageName,
			"project": rootName,
			"color":   NodeColors["Package"],
			"url":     createUrl(baseUrl, filePath, projectRoot, 1),
		})
	if err != nil {
		log.Printf("Failed to create package node: %v", err)
		return
	}

	// Traverse the AST
	ast.Inspect(node, func(n ast.Node) bool {
		switch x := n.(type) {
		case *ast.GenDecl:
			processGenDecl(ctx, x, packageId, filePath, fset, rootName, session)
		case *ast.FuncDecl:
			processFuncDecl(ctx, x, packageId, filePath, fset, rootName, session)
		}
		return true
	})
}

// Process generic declarations like Structs, Interfaces
func processGenDecl(ctx context.Context, x *ast.GenDecl, packageId, filePath string, fset *token.FileSet, rootName string, session neo4j.SessionWithContext) {
	for _, spec := range x.Specs {
		if typeSpec, ok := spec.(*ast.TypeSpec); ok {
			typeName := typeSpec.Name.Name
			typeId := fmt.Sprintf("%s.%s", packageId, typeName)

			switch typeSpec.Type.(type) {
			case *ast.StructType:
					_, err := session.Run(ctx,
					"MERGE (s:Struct {id: $id}) "+
						"ON CREATE SET s.name = $name, s.packageId = $packageId, s.project = $project, s.color = $color, s.url = $url",
					map[string]interface{}{
						"id":        typeId,
						"name":      typeName,
						"packageId": packageId,
						"project":   rootName,
						"color":     NodeColors["Struct"],
						"url":       createUrl("/app", filePath, "/app", fset.Position(x.Pos()).Line),
					})
				if err != nil {
					log.Printf("Failed to create struct node: %v", err)
				}
			}
		}
	}
}

// Process function and method declarations
func processFuncDecl(ctx context.Context, x *ast.FuncDecl, packageId, filePath string, fset *token.FileSet, rootName string, session neo4j.SessionWithContext) {
	funcName := x.Name.Name
	funcSignature := funcName // Extend with parameters if needed
	var funcId string

	if x.Recv != nil && len(x.Recv.List) > 0 {
		recvType := extractReceiverType(x.Recv)
		if recvType != "" {
			structId := fmt.Sprintf("%s.%s", packageId, recvType)
			funcId = fmt.Sprintf("%s.%s", structId, funcSignature)

			// Create or merge Method node
			_, err := session.Run(ctx,
				"MERGE (m:Method {id: $id}) "+
					"ON CREATE SET m.name = $name, m.structId = $structId, m.project = $project, m.color = $color, m.url = $url",
				map[string]interface{}{
					"id":       funcId,
					"name":     funcName,
					"structId": structId,
					"project":  rootName,
					"color":    NodeColors["Method"],
					"url":      createUrl("/app", filePath, "/app", fset.Position(x.Pos()).Line),
				})
			if err != nil {
				log.Printf("Failed to create method node: %v", err)
			}
		}
	} else {
		funcId = fmt.Sprintf("%s.%s", packageId, funcSignature)

		// Create or merge Function node
		_, err := session.Run(ctx,
			"MERGE (f:Function {id: $id}) "+
				"ON CREATE SET f.name = $name, f.packageId = $packageId, f.project = $project, f.color = $color, f.url = $url",
			map[string]interface{}{
				"id":        funcId,
				"name":      funcName,
				"packageId": packageId,
				"project":   rootName,
				"color":     NodeColors["Function"],
				"url":       createUrl("/app", filePath, "/app", fset.Position(x.Pos()).Line),
			})
		if err != nil {
			log.Printf("Failed to create function node: %v", err)
		}
	}
}

// Helper function to extract receiver type from a method declaration
func extractReceiverType(recv *ast.FieldList) string {
	if len(recv.List) == 0 {
		return ""
	}
	switch expr := recv.List[0].Type.(type) {
	case *ast.StarExpr:
		if ident, ok := expr.X.(*ast.Ident); ok {
			return ident.Name
		}
	case *ast.Ident:
		return expr.Name
	}
	return ""
}
