using Synapse.Listing.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddSingleton<ListingService>();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

WebApplication app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

ConfigurationManager config = builder.Configuration;
if (config.GetValue<bool>("StaticFiles:Enabled"))
{
    StaticFileOptions options = new();
    config.Bind("StaticFiles", options);
    app.Logger.LogInformation("Serving static files from [{EnvironmentWebRootPath}]", app.Environment.WebRootPath);
    app.UseStaticFiles(options);

}

////app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
