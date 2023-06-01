using Autodesk.Forge;
using Microsoft.AspNetCore.Mvc;

namespace DesignAutomationApp.Controllers
{
    [ApiController]
    public class OAuthController : ControllerBase
    {
        public static dynamic InternalToken { get; set; }


        ///<summary>
        ///Get access token with internal (write) scope
        ///</summary>
        public static async Task<dynamic> GetInternalAsync()
        {
            if (InternalToken == null || InternalToken.ExpiresAt < DateTime.UtcNow)
            {
                InternalToken = await Get2LeggedTokenAsync(
                    new Scope[]
                    {
                        Scope.BucketCreate,
                        Scope.BucketRead,
                        Scope.BucketDelete,
                        Scope.DataRead,
                        Scope.DataWrite,
                        Scope.DataCreate,
                        Scope.CodeAll
                    });
                InternalToken.ExpiresAt = DateTime.UtcNow.AddSeconds(InternalToken.expires_in);
            }

            return InternalToken;
        }

        ///<summary>
        ///Get the access token from Autodesk
        ///</summary>
        private static async Task<dynamic> Get2LeggedTokenAsync(Scope[] scopes)
        {
            TwoLeggedApi oauth = new TwoLeggedApi();
            string grantType = "client_credentials";

            dynamic bearer = await oauth.AuthenticateAsync(
                GetAppSetting("APS_CLIENT_ID"),
                GetAppSetting("APS_CLIENT_SECRET"),
                grantType,
                scopes);

            return bearer;
        }

        /// <summary>
        /// Reads appsettings from web.config
        /// </summary>
        public static string GetAppSetting(string settingKey)
        {
            return Environment.GetEnvironmentVariable(settingKey).Trim();
        }
    }
}
