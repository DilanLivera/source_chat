# Source Chat

A .NET console application for ingesting and querying code documentation using RAG (Retrieval-Augmented Generation).

Built using [System.CommandLine](https://learn.microsoft.com/en-us/dotnet/standard/commandline/get-started-tutorial) library and [Microsoft.Extensions.DataIngestion](https://learn.microsoft.com/en-us/dotnet/ai/conceptual/data-ingestion) package.

## Architecture

SourceChat follows a Vertical Slice Architecture, organizing code around distinct business features (Ingest, Query, List, Config, Clear) rather than technical layers. Each feature folder contains its specific commands and services, promoting high cohesion and easier maintainability. Technical concerns like storage, parsing, and configuration are isolated in the Infrastructure layer, while common logic is shared via the Features/Shared directory.

### Project Structure

```text
src/SourceChat/
├── Features/               # Business capabilities
│   ├── Shared/             # Common logic and models
│   ├── Ingest/             # Data ingestion feature
│   ├── Query/              # RAG-based querying feature
│   ├── List/               # File listing and stats
│   ├── Config/             # Configuration management
│   └── Clear/              # Data cleanup
├── Infrastructure/         # Technical concerns
│   ├── Configuration/      # Env/Settings handling
│   ├── Storage/            # Vector store and file tracking
│   └── Parsing/            # File parsers (C#, MD, JSON, etc.)
└── Program.cs              # Application entry point
```

## Installation & Setup

### Prerequisites

- .NET 9.0 or later
- OpenAI API key, Azure OpenAI credentials, or Ollama running locally

### Environment Configuration

Create `.env.example`:

```bash
# AI Provider (OpenAI, AzureOpenAI, or Ollama)
AI_PROVIDER=OpenAI

# OpenAI Configuration
OPENAI_API_KEY=your-api-key-here
OPENAI_CHAT_MODEL=gpt-4
OPENAI_EMBEDDING_MODEL=text-embedding-3-small

# Azure OpenAI Configuration
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-api-key-here
AZURE_OPENAI_CHAT_DEPLOYMENT=gpt-4
AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-3-small

# Ollama Configuration
OLLAMA_ENDPOINT=http://localhost:11434
OLLAMA_CHAT_MODEL=llama3.2
OLLAMA_EMBEDDING_MODEL=all-minilm

# Database Configuration
SQLITE_DB_PATH=./sourcechat.db

# Default Settings
DEFAULT_CHUNKING_STRATEGY=semantic
MAX_TOKENS_PER_CHUNK=2000
CHUNK_OVERLAP_TOKENS=200
```

Copy to `.env` and configure:

```bash
cp .env.example .env
# Edit .env with your actual values
```

## Usage Guide

### 1. Setup

```bash
# Clone project
git clone https://github.com/DilanLivera/source_chat
cd source_chat/src/SourceChat

# Install packages
dotnet restore

# Create .env file
cp .env.example .env
# Edit .env with your configuration
```

### 2. Build

```bash
dotnet build --no-restore
```

### 3. Basic Usage

```bash
# Ingest code from a directory
dotnet run -- ingest ./my-project --strategy Semantic

# Query the codebase (single question)
dotnet run -- query "How does authentication work in this codebase?"

# Interactive mode
dotnet run -- query --interactive

# List ingested files
dotnet run -- list

# Show statistics
dotnet run -- list --stats

# Clear all data
dotnet run -- clear --confirm

# Show configuration
dotnet run -- config
```

### 4. Advanced Usage

```bash
# Ingest with specific file patterns
dotnet run -- ingest ./src --patterns "*.cs;*.md" --strategy Structure

# Verbose ingestion output
dotnet run -- ingest ./src --verbose

# Force full re-ingestion (not incremental)
dotnet run -- ingest ./src --incremental false

# Query with more results
dotnet run -- query "Explain the database layer" --max-results 10
```

### 5. Example Workflow

```bash
# 1. Configure environment
export AI_PROVIDER=OpenAI
export OPENAI_API_KEY=sk-...
export SQLITE_DB_PATH=./myproject.db

# 2. Ingest your codebase
dotnet run -- ingest ~/Projects/MyApp --verbose

# 3. Start querying
dotnet run -- query --interactive
```

### Interactive Query Example

```
=== SourceChat Interactive Mode ===
Ask questions about your codebase. Type 'exit' to quit, 'clear' to reset conversation.

You: What authentication methods are used in this project?

SourceChat: Based on the codebase, this project uses JWT (JSON Web Token) authentication.
The AuthenticationService class in Services/AuthenticationService.cs handles token generation
and validation. The tokens are configured with a 24-hour expiration time.

You: How are the tokens validated?

SourceChat: The token validation is performed in the JwtMiddleware class. It extracts the
token from the Authorization header, validates it using the TokenValidationParameters, and
attaches the user identity to the HttpContext if validation succeeds. Invalid tokens result
in a 401 Unauthorized response.

You: clear
Conversation history cleared.

You: exit
Goodbye!
```

## Troubleshooting

**Issue**: "OPENAI_API_KEY not set"

- Solution: Create .env file or set environment variable

**Issue**: "No files found matching patterns"

- Solution: Check your --patterns option and directory path

**Issue**: "Database locked"

- Solution: Close any other processes using the database

**Issue**: Slow ingestion

- Solution: Use --incremental flag or reduce chunk overlap

**Issue**: Poor query results

- Solution: Try different chunking strategies or adjust max tokens per chunk
-
