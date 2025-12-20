package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"os"
	"path/filepath"

	"github.com/neo4j/neo4j-go-driver/v5/neo4j"
	"github.com/theapemachine/platform-graph/graphlang"
)

type projectConfig struct {
	Name        string `json:"name"`
	Path        string `json:"path"`
	BaseURL     string `json:"baseURL"`
	ProjectRoot string `json:"projectRoot"`
}

type configFile struct {
	WorkspaceRoot string          `json:"workspaceRoot"`
	Projects      []projectConfig `json:"projects"`
}

func main() {
	defaultConfig := os.Getenv("PROJECTS_CONFIG")
	defaultRootName := os.Getenv("ROOT_NAME")
	defaultBaseURL := os.Getenv("BASE_URL")
	defaultPath := os.Getenv("ANALYZE_PATH")
	defaultProjectRoot := os.Getenv("PROJECT_ROOT")

	if defaultRootName == "" {
		defaultRootName = "UnknownRoot"
	}
	if defaultBaseURL == "" {
		defaultBaseURL = "http://localhost"
	}
	if defaultPath == "" {
		defaultPath = "/app"
	}

	configPath := flag.String("config", defaultConfig, "Path to a JSON config file describing one or more projects")
	rootName := flag.String("root-name", defaultRootName, "Project name/root label")
	baseURL := flag.String("base-url", defaultBaseURL, "Base URL for source links")
	analyzePath := flag.String("path", defaultPath, "Directory to analyze")
	projectRoot := flag.String("project-root", defaultProjectRoot, "Directory root used for building source links (defaults to --path)")
	flag.Parse()

	neo4jURI := os.Getenv("NEO4J_URI")
	neo4jUser := os.Getenv("NEO4J_USER")
	neo4jPassword := os.Getenv("NEO4J_PASSWORD")
	if neo4jURI == "" || neo4jUser == "" || neo4jPassword == "" {
		log.Fatal("NEO4J_URI, NEO4J_USER and NEO4J_PASSWORD must be set")
	}

	log.Printf("Connecting to Neo4j at %s as %s\n", neo4jURI, neo4jUser)
	driver, err := neo4j.NewDriverWithContext(neo4jURI, neo4j.BasicAuth(neo4jUser, neo4jPassword, ""))
	if err != nil {
		log.Fatalf("Failed to create Neo4j driver: %v", err)
	}
	defer driver.Close(context.Background())

	if *configPath != "" {
		if err := runFromConfig(driver, *configPath); err != nil {
			log.Fatal(err)
		}
		return
	}

	pr := *projectRoot
	if pr == "" {
		pr = *analyzePath
	}

	parser := graphlang.NewTreeSitterParser(driver, *rootName, *baseURL, pr)
	if err := parser.AnalyzeDirectory(*analyzePath); err != nil {
		log.Fatal(err)
	}
}

func runFromConfig(driver neo4j.DriverWithContext, path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return fmt.Errorf("read config %q: %w", path, err)
	}

	var cfg configFile
	if err := json.Unmarshal(data, &cfg); err != nil {
		return fmt.Errorf("parse config %q: %w", path, err)
	}
	if len(cfg.Projects) == 0 {
		return fmt.Errorf("config %q: no projects defined", path)
	}
	if cfg.WorkspaceRoot == "" {
		cfg.WorkspaceRoot = "/workspace"
	}

	var hadError bool
	for _, project := range cfg.Projects {
		if project.Name == "" {
			log.Printf("Skipping project with empty name (path=%q)", project.Path)
			hadError = true
			continue
		}
		if project.Path == "" {
			log.Printf("Skipping project %q with empty path", project.Name)
			hadError = true
			continue
		}

		analyzePath := project.Path
		if !filepath.IsAbs(analyzePath) {
			analyzePath = filepath.Join(cfg.WorkspaceRoot, analyzePath)
		}

		projectRoot := project.ProjectRoot
		if projectRoot == "" {
			projectRoot = analyzePath
		} else if !filepath.IsAbs(projectRoot) {
			projectRoot = filepath.Join(cfg.WorkspaceRoot, projectRoot)
		}

		baseURL := project.BaseURL
		if baseURL == "" {
			baseURL = "http://localhost"
		}

		log.Printf("Analyzing %q at %s", project.Name, analyzePath)
		parser := graphlang.NewTreeSitterParser(driver, project.Name, baseURL, projectRoot)
		if err := parser.AnalyzeDirectory(analyzePath); err != nil {
			log.Printf("Error analyzing %q: %v", project.Name, err)
			hadError = true
		}
	}

	if hadError {
		return fmt.Errorf("one or more projects failed")
	}
	return nil
}
