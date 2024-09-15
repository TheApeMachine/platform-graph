# Use the official Node.js image
FROM node:18

# Set working directory
WORKDIR /analyzer

# {{ edit_1 }}
# Remove hard-coded environment variables
# ENV NEO4J_URI=bolt://host.docker.internal:7687
# ENV NEO4J_USER=neo4j
# ENV NEO4J_PASSWORD=securepassword
# {{ edit_1 }}

# Copy package.json and install dependencies
COPY package.json package-lock.json ./
RUN npm install

# Copy the rest of the application code
COPY . .

# Command to run the analyzer
CMD ["node", "analyzer.js"]
