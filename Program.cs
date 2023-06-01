using Autodesk.Forge.DesignAutomation;

namespace DesignAutomationApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Configuration.AddJsonFile(
                $"appsettings.user.json", 
                optional: true);

            builder.Configuration.AddEnvironmentVariables();
            
            builder.Services
                .AddMvc(options => options.EnableEndpointRouting = false)
                .AddNewtonsoftJson();
            
            builder.Services.AddSignalR().AddNewtonsoftJsonProtocol(opt =>
            {
                opt.PayloadSerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
            });
            
            builder.Services.AddDesignAutomation(builder.Configuration);

            builder.Services.AddControllers();

            var app = builder.Build();

            app.UseFileServer();
            app.UseMvc();

            app.UseRouting();
            app.UseEndpoints(routes =>
            {
                routes.MapHub<Controllers.DesignAutomationHub>("/api/signalr/designautomation");
            });

            app.Run();
        }
    }
}