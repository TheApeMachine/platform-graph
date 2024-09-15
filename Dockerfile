# Use an official Golang image as a parent image
FROM golang:1.23 AS builder

# Set the working directory inside the container
WORKDIR /analyzer

# {{ edit_1 }}
# Remove hard-coded environment variables
# ENV NEO4J_URI=bolt://host.docker.internal:7687
# ENV NEO4J_USER=neo4j
# ENV NEO4J_PASSWORD=securepassword
# {{ edit_1 }}

# ... existing COPY and RUN commands ...