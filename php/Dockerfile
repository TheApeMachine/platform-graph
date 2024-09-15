FROM php:8.1-cli

# {{ edit_1 }}
# Remove hard-coded environment variables
# ENV NEO4J_URI=bolt://host.docker.internal:7687
# ENV NEO4J_USER=neo4j
# ENV NEO4J_PASSWORD=securepassword
# {{ edit_1 }}

# Install required extensions and dependencies
RUN apt-get update && apt-get install -y \
    git \
    unzip \
    libzip-dev \
    iputils-ping \
    && docker-php-ext-install zip bcmath

# Install Composer
RUN curl -sS https://getcomposer.org/installer | php -- --install-dir=/usr/local/bin --filename=composer

# Set working directory
WORKDIR /analyzer

# Copy composer files and install dependencies
COPY composer.json composer.lock ./
RUN composer update && composer install --no-scripts --no-autoloader --ignore-platform-req=ext-bcmath

# Copy the rest of the application
COPY . .

# Generate autoloader
RUN composer dump-autoload --optimize

RUN composer require --with-all-dependencies  laudis/neo4j-php-client

# Run the analyzer
CMD ["php", "src/analyzer.php"]