namespace MySale.AI.Infrastructure;

public sealed class SystemDbOptions
{
    public const string Section = "SystemDb";
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "mysale_ai_system";
}

/// <summary>Bootstrap business database. Overridden by the configuration saved from the Database page.</summary>
public sealed class BusinessDbOptions
{
    public const string Section = "BusinessDb";
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "mysale_ai_test";
}

public sealed class AuthOptions
{
    public const string Section = "Auth";
    /// <summary>HMAC signing key (min 32 chars). Set via user-secrets / environment in real deployments.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "mysale-ai-agent";
    public int TokenLifetimeMinutes { get; set; } = 480;
    /// <summary>Lists demo accounts on the login screen. Disable outside local testing.</summary>
    public bool ExposeDemoUsers { get; set; } = true;
}

public sealed class SeedOptions
{
    public const string Section = "Seed";
    public bool SeedSystemDataOnStartup { get; set; } = true;
    public bool SeedBusinessDataOnStartup { get; set; } = true;
    public string DemoPassword { get; set; } = "Demo@123";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.1";
    public string OpenAIModel { get; set; } = "gpt-4o-mini";
    /// <summary>Optional: encrypted into the OpenAI provider on first start.</summary>
    public string? OpenAIApiKey { get; set; }
}
