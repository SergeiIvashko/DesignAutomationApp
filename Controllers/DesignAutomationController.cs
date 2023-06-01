using Autodesk.Forge.DesignAutomation;
using Autodesk.Forge.DesignAutomation.Model;
using Autodesk.Forge.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json.Linq;
using Activity = Autodesk.Forge.DesignAutomation.Model.Activity;
using Alias = Autodesk.Forge.DesignAutomation.Model.Alias;
using AppBundle = Autodesk.Forge.DesignAutomation.Model.AppBundle;
using Parameter = Autodesk.Forge.DesignAutomation.Model.Parameter;
using WorkItem = Autodesk.Forge.DesignAutomation.Model.WorkItem;
using WorkItemStatus = Autodesk.Forge.DesignAutomation.Model.WorkItemStatus;

namespace DesignAutomationApp.Controllers
{
    [ApiController]
    public class DesignAutomationController : ControllerBase
    {
        private readonly IWebHostEnvironment env;
        private readonly IHubContext<DesignAutomationHub> hubContext;
        readonly DesignAutomationClient designAutomation;

        private static PostCompleteS3UploadPayload postCompleteS3UploadPayload;

        public DesignAutomationController(IWebHostEnvironment env, IHubContext<DesignAutomationHub> hubContext, DesignAutomationClient api)
        {
            this.env = env;
            this.hubContext = hubContext;
            designAutomation = api;
        }
        public string LocalBundlesFolder
        {
            get
            {
                return Path.Combine(this.env.WebRootPath, "bundles");
            }
        }

        public static string NickName
        {
            get
            {
                return OAuthController.GetAppSetting("APS_CLIENT_ID");
            }
        }

        public static string Alias
        {
            get
            {
                return "dev";
            }
        }

        public static PostCompleteS3UploadPayload S3UploadPayload
        {
            get
            {
                return postCompleteS3UploadPayload;
            }
            set
            {
                postCompleteS3UploadPayload = value;
            }
        }

        /// <summary>
        /// Names of app bundles on this project
        /// </summary>
        [HttpGet]
        [Route("api/appbundles")]
        public string[] GetLocalBundles()
        {
            return Directory.GetFiles(LocalBundlesFolder, "*.zip")
                            .Select(Path.GetFileNameWithoutExtension)
                            .ToArray();
        }

        /// <summary>
        /// Return a list of available engines
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("api/aps/designautomation/engines")]
        public async Task<List<string>> GetAvailableEngines()
        {
            dynamic oauth = await OAuthController.GetInternalAsync();
            List<string> allEngines = new List<string>();

            string paginationToken = null;
            while (true)
            {
                Page<string> engines = await designAutomation.GetEnginesAsync(paginationToken);
                allEngines.AddRange(engines.Data);

                if (engines.PaginationToken == null)
                {
                    break;
                }
                paginationToken = engines.PaginationToken;
            }

            allEngines.Sort();
            return allEngines;
        }

        /// <summary>
        /// Define a new appbundle
        /// </summary>
        [HttpPost]
        [Route("api/aps/designautomation/appbundles")]
        public async Task<IActionResult> CreateAppBundle([FromBody] JObject appBundleSpecs)
        {
            string zipFileName = appBundleSpecs["zipFileName"].Value<string>();
            string engineName = appBundleSpecs["engine"].Value<string>();

            string appBundleName = zipFileName + "AppBundle";

            string packageZipPath = Path.Combine(LocalBundlesFolder, zipFileName + ".zip");
            if (!System.IO.File.Exists(packageZipPath))
            {
                throw new ArgumentException("Appbundle not found at " +  packageZipPath);
            }

            Page<string> appBundles = await designAutomation.GetAppBundlesAsync();

            dynamic newAppVersion;
            string qualifiedAppBundleId = string.Format($"{NickName}.{appBundleName}+{Alias}");
            if (!appBundles.Data.Contains(qualifiedAppBundleId))
            {
                AppBundle appBundleSpec = new AppBundle()
                {
                    Package = appBundleName,
                    Engine = engineName,
                    Id = appBundleName,
                    Description = string.Format($"Description for {appBundleName}")
                };

                newAppVersion = await designAutomation.CreateAppBundleAsync(appBundleSpec);
                if (newAppVersion == null)
                {
                    throw new ArgumentException("Cannot create new app");
                }

                Alias aliasSpec = new Alias() { Id = Alias, Version = 1 };
                Alias newAlias = await designAutomation.CreateAppBundleAliasAsync(appBundleName, aliasSpec);
            }
            else
            {
                AppBundle appBundleSpec = new AppBundle()
                {
                    Engine = engineName,
                    Description = appBundleName
                };

                newAppVersion = await designAutomation.CreateAppBundleVersionAsync(appBundleName, appBundleSpec);
                if (newAppVersion == null)
                {
                    throw new ArgumentException("Cannot create new version");
                }

                AliasPatch aliasSpec = new AliasPatch()
                {
                    Version = newAppVersion.Version
                };

                Alias newAlias = await designAutomation.ModifyAppBundleAliasAsync(appBundleName, Alias, aliasSpec);
            }

            using (var client = new HttpClient())
            {
                using (var formData = new MultipartFormDataContent(engineName))
                {
                    foreach (var kv in newAppVersion.UploadParameters.FormData)
                    {
                        if (kv.Value != null)
                        {
                            formData.Add(new StringContent(kv.Value), kv.Key);
                        }
                    }

                    using (var content = new StreamContent(new FileStream(packageZipPath, FileMode.Open)))
                    {
                        formData.Add(content, "file");
                        using (var request = new HttpRequestMessage(HttpMethod.Post, newAppVersion.UploadParameters.EndpointURL) { Content = formData })
                        {
                            var response = await client.SendAsync(request);
                            response.EnsureSuccessStatusCode();
                        }
                    }
                }
            }

            return Ok(new {AppBundle = qualifiedAppBundleId, Version = newAppVersion.Version});
        }
    }

    /// <summary>
    /// Class uses for SignalR
    /// </summary>
    public class DesignAutomationHub : Microsoft.AspNetCore.SignalR.Hub
    {
        public string GetConnectionId()
        {
            return Context.ConnectionId;
        }
    }
}
