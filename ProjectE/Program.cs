using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ProjectE;
using ProjectE.ChatBotInteraction;
using ProjectE.Data;
using ProjectE.Services;
using System.Text;
using System.Text.Json.Serialization;

/// <summary>
/// Entry point for the application.
/// Configures services, middleware, and HTTP request pipeline.
/// </summary>
var builder = WebApplication.CreateBuilder(args);

/// <summary>
/// Adds services to the dependency injection container.
/// </summary>
builder.Services.AddControllersWithViews();
builder.Services.AddControllers().AddJsonOptions(opts =>
{
    opts.JsonSerializerOptions.Converters.Clear(); // remove enum-to-string converter if added anywhere
});

builder.Services.AddSession(); //  session handling
builder.Services.AddScoped<IChatbotResponseService, ChatbotResponseService>();
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var endpoint = new Uri(cfg["AzureOpenAI:Endpoint"]!);
    var key = new AzureKeyCredential(cfg["AzureOpenAI:ApiKey"]!);
    return new AzureOpenAIClient(endpoint, key);
});
// Use dependency injection 
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IMenuService, MenuService>();


builder.Services.AddScoped<IOrderService, OrderService>();

builder.Services.AddScoped<IRecommendationService, RecommendationService>();
builder.Services.AddHostedService<DraftOrderCleanupService>();


// Register custom AAD auth provider
SqlAuthenticationProvider.SetProvider(
    SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow,
    new CustomAzureAuthenticator()
);

// Add DbContext with Azure SQL connection
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("newAzureDBCS")));
Console.WriteLine("Connection");
using (var scope = builder.Services.BuildServiceProvider().CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        if (dbContext.Database.CanConnect())
        {
            logger.LogInformation("Connection to Azure SQL succeeded!");
        }
        else
        {
            logger.LogWarning("Connection to Azure SQL failed (CanConnect returned false).");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to connect to Azure SQL.");
    }
}


// Authentication - JWT Bearer
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var key = Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key)
        };
    });

/// <summary>
/// Adds Razor Pages services to the application.
/// </summary>
builder.Services.AddRazorPages();

/// <summary>
/// Builds the application pipeline.
/// </summary>
var app = builder.Build();

/// <summary>
/// Configures middleware to handle HTTP requests.
/// </summary>
if (!app.Environment.IsDevelopment())
{
    /// <summary>
    /// Use the error handler page in non-development environments.
    /// </summary>
    app.UseExceptionHandler("/Home/Error");

    /// <summary>
    /// Use HTTP Strict Transport Security (HSTS) middleware.
    /// </summary>
    app.UseHsts();
}

/// <summary>
/// Redirect HTTP requests to HTTPS.
/// </summary>
app.UseHttpsRedirection();

/// <summary>
/// Enables serving of static files such as CSS, JS, images.
/// </summary>
app.UseStaticFiles();

/// <summary>
/// Adds routing middleware to route HTTP requests.
/// </summary>
app.UseRouting();

/// <summary>
/// Enables session middleware to track session data.
/// </summary>
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Auth}/{action=Login}/{id?}")
    .WithStaticAssets();

app.Run();