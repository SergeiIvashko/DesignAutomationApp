using Autodesk.Forge;
using Autodesk.Forge.Core;
using Autodesk.Forge.DesignAutomation;
using Autodesk.Forge.DesignAutomation.Model;
using Autodesk.Forge.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
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
                throw new ArgumentException("Appbundle not found at " + packageZipPath);
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

            return Ok(new { AppBundle = qualifiedAppBundleId, Version = newAppVersion.Version });
        }

        /// <summary>
        /// Define a new activity
        /// </summary>
        [HttpPost]
        [Route("api/aps/designautomation/activities")]
        public async Task<IActionResult> CreateActivity([FromBody] JObject activitySpecs)
        {
            string zipFileName = activitySpecs["zipFileName"].Value<string>();
            string engineName = activitySpecs["engine"].Value<string>();

            string appBundleName = zipFileName + "AppBundle";
            string activityName = zipFileName + "Activity";

            Page<string> activities = await designAutomation.GetActivitiesAsync();
            string qualifiedActivityId = string.Format($"{NickName}.{activityName}+{Alias}");

            if (!activities.Data.Contains(qualifiedActivityId))
            {
                dynamic engineAttributes = EngineAttributes(engineName);
                string commandLine = string.Format(engineAttributes.commandLine, appBundleName);
                Activity activitySpec = new Activity()
                {
                    Id = activityName,
                    Appbundles = new List<string>()
                    {
                        string.Format($"{NickName}.{appBundleName}+{Alias}")
                    },
                    CommandLine = new List<string>()
                    {
                        commandLine
                    },
                    Engine = engineName,
                    Parameters = new Dictionary<string, Parameter>()
                    {
                        { "inputFile", new Parameter() { Description = "input file", LocalName = "$(inputFile)", Ondemand = false, Required = true, Verb = Verb.Get, Zip = false } },
                        { "inputJson", new Parameter() { Description = "input json", LocalName = "params.json", Ondemand = false, Required = false, Verb = Verb.Get, Zip = false } },
                        { "outputFile", new Parameter() { Description = "output file", LocalName = "outputFile." + engineAttributes.extension, Ondemand = false, Required = true, Verb = Verb.Put, Zip = false } }
                    },
                    Settings = new Dictionary<string, ISetting>()
                    {
                        { "script", new StringSetting() {Value = engineAttributes.script } }
                    }
                };

                Activity newActivity = await designAutomation.CreateActivityAsync(activitySpec);

                Alias aliasSpec = new Alias() { Id = Alias, Version = 1 };
                Alias newAlias = await designAutomation.CreateActivityAliasAsync(activityName, aliasSpec);

                return Ok(new { Activity = qualifiedActivityId });
            }

            return Ok(new { Activity = "Activity already defined" });
        }


        [HttpGet]
        [Route("api/aps/designautomation/activities")]
        public async Task<List<string>> GetDefinedActivities()
        {
            Page<string> activities = await designAutomation.GetActivitiesAsync();
            List<string> definedActivities = new List<string>();
            foreach (string activity in activities.Data)
            {
                if (activity.StartsWith(NickName) && activity.IndexOf("$LATEST") == -1)
                {
                    definedActivities.Add(activity.Replace(NickName + ".", String.Empty));
                }
            }

            return definedActivities;
        }

        /// <summary>
        /// Clear the accounts (for debugging purposes)
        /// </summary>
        [HttpDelete]
        [Route("api/aps/designautomation/account")]
        public async Task<IActionResult> ClearAccount()
        {
            await designAutomation.DeleteForgeAppAsync("me");
            return Ok();
        }

        /// <summary>
        /// Direct To S3
        /// </summary>
        static void onUploadProgress(float progress, TimeSpan elapsed, List<UploadItemDesc> objects)
        {
            Console.WriteLine($"progress: {progress} elapsed: {elapsed} objects: {string.Join(".", objects)}");
        }

        public static async Task<string> GetObjectId(string bucketKey,
                                                     string objectKey,
                                                     dynamic oauth,
                                                     string fileSavePath)
        {
            try
            {
                ObjectsApi objectsApi= new ObjectsApi();
                objectsApi.Configuration.AccessToken = oauth.access_token;
                List<UploadItemDesc> uploadRes = await objectsApi.uploadResources(
                    bucketKey,
                    new List<UploadItemDesc>
                    {
                        new UploadItemDesc(objectKey, await System.IO.File.ReadAllBytesAsync(fileSavePath))
                    },
                    null,
                    onUploadProgress,
                    null);
                Console.WriteLine("**** Upload object(s) response(s):");
                DynamicDictionary objValues = uploadRes[0].completed;
                objValues.Dictionary.TryGetValue("objectId", out var id);

                return id?.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception when preparing input url:{ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Start a new workitem
        /// </summary>
        [HttpPost]
        [Route("api/aps/designautomation/workitems")]
        public async Task<IActionResult> StartWorkitem([FromForm] StartWorkitemInput input)
        {
            JObject workItemData = JObject.Parse(input.data);
            string widthParam = workItemData["width"].Value<string>();
            string heightParam = workItemData["height"].Value<string>();
            string activityName = string.Format($"{NickName}.{workItemData["activityName"].Value<string>()}");
            string browserConnectionId = workItemData["browserConnectionId"].Value<string>();

            var fileSavePath = Path.Combine(this.env.ContentRootPath, Path.GetFileName(input.inputFile.FileName));
            using (var stream = new FileStream(fileSavePath, FileMode.Create))
            {
                await input.inputFile.CopyToAsync(stream);
            }

            dynamic oauth = await OAuthController.GetInternalAsync();

            string bucketKey = NickName.ToLower() + "-designautomation";
            BucketsApi buckets = new BucketsApi();
            buckets.Configuration.AccessToken = oauth.access_token;
            try
            {
                PostBucketsPayload bucketsPayload = new PostBucketsPayload(bucketKey, null,
                                                                           PostBucketsPayload.PolicyKeyEnum.Transient);
                await buckets.CreateBucketAsync(bucketsPayload, "US");
            }
            catch (Exception ex)
            {
            }

            string inputFileNameOSS = string.Format($"{DateTime.Now.ToString("yyyyMMddhhmmss")}_input_{Path.GetFileName(input.inputFile.FileName)}");

            XrefTreeArgument inputFileArgument = new XrefTreeArgument()
            {
                Url = await GetObjectId(bucketKey, inputFileNameOSS, oauth, fileSavePath),
                Headers = new Dictionary<string, string>()
                {
                    {"Authorization", "Bearer" + oauth.access_token}
                }
            };

            dynamic inputJson = new JObject();
            inputJson.Width = widthParam;
            inputJson.Height = heightParam;
            XrefTreeArgument inputJsonArgument = new XrefTreeArgument()
            {
                Url = "data:application/json, " + ((JObject)inputJson).ToString(Formatting.None).Replace("\"", "'")
            };

            string outputFileNameOSS = string.Format($"{DateTime.Now.ToString("yyyyMMddhhmmss")}_output_{Path.GetFileName(input.inputFile.FileName)}");

            XrefTreeArgument outputFileArgument = new XrefTreeArgument()
            {
                Url = await GetObjectId(bucketKey, outputFileNameOSS, oauth, fileSavePath),
                Headers = new Dictionary<string, string>()
                {
                    { "Authorization", "Bearer" + oauth.access_token }
                },
                Verb = Verb.Put
            };

            if (System.IO.File.Exists(fileSavePath))
            {
                System.IO.File.Delete(fileSavePath);
            }

            string callbackUrl = string.Format("{0}/api/aps/callback/designautomation?id={1}&outputFileName={2}",
                                               OAuthController.GetAppSetting("APS_WEBHOOK_URL"), browserConnectionId,
                                               outputFileNameOSS);

            WorkItem workItemSpec = new WorkItem()
            {
                ActivityId = activityName,
                Arguments = new Dictionary<string, IArgument>()
                {
                    { "inputFile", inputFileArgument },
                    { "inputJson", inputJsonArgument },
                    { "outputFile", outputFileArgument },
                    { "onComplete", new XrefTreeArgument { Verb = Verb.Post, Url = callbackUrl} }
                }
            };
            WorkItemStatus workItemStatus = await designAutomation.CreateWorkItemAsync(workItemSpec);

            return Ok(new { WorkItemId = workItemStatus.Id });
        }

        [HttpPost]
        [Route("api/aps/callback/designautomation")]
        public async Task<IActionResult> OnCallback(string id, string outputFileName, [FromBody] dynamic body)
        {
            try
            {
                JObject bodyJson = JObject.Parse((string)body.ToString());
                await this.hubContext.Clients.Client(id).SendAsync("onComplete", bodyJson.ToString());

                using (var httpClient = new HttpClient())
                {
                    byte[] bs = await httpClient.GetByteArrayAsync(bodyJson["reportUrl"]?.Value<string>());
                    string report = System.Text.Encoding.Default.GetString(bs);
                    await this.hubContext.Clients.Client(id).SendAsync("onComplete", report);
                }

                dynamic oauth = await OAuthController.GetInternalAsync();

                ObjectsApi objectsApi = new ObjectsApi();
                objectsApi.Configuration.AccessToken = oauth.access_token;

                Autodesk.Forge.Client.ApiResponse<dynamic> res = await objectsApi.getS3DownloadURLAsyncWithHttpInfo(
                                            NickName.ToLower() + "-designautomation",
                                            outputFileName, new Dictionary<string, object> {
                                            { "minutesExpiration", 15.0 },
                                            { "useCdn", true }
                                            });

                await this.hubContext.Clients.Client(id).SendAsync("downloadResult", (string)(res.Data.url));
                Console.WriteLine("Congrats!");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return Ok();
        }

        /// <summary>
        /// Helps identify the engine
        /// </summary>
        private dynamic EngineAttributes(string engine)
        {
            if (engine.Contains("3dsMax"))
            {
                return new
                {
                    commandLine = "$(engine.path)\\3dsmaxbatch.exe -sceneFile \"$(args[inputFile].path)\" $(settings[script].path)",
                    extension = "max",
                    script = "da = dotNetClass(\"Autodesk.Forge.Sample.DesignAutomation.Max.RuntimeExecute\")\nda.ModifyWindowWidthHeight()\n"
                };
            }

            if (engine.Contains("AutoCAD"))
            {
                return new
                {
                    commandLine = "$(engine.path)\\accoreconsole.exe /i \"$(args[inputFile].path)\" /al \"$(appbundles[{0}].path)\" /s $(settings[script].path)",
                    extension = "dwg",
                    script = "UpdateParam\n"
                };
            }

            if (engine.Contains("Inventor"))
            {
                return new
                {
                    commandLine = "$(engine.path)\\inventorcoreconsole.exe /i \"$(args[inputFile].path)\" /al \"$(appbundles[{0}].path)\"",
                    extension = "ipt",
                    script = string.Empty
                };
            }

            if (engine.Contains("Revit"))
            {
                return new
                {
                    commandLine = "$(engine.path)\\revitcoreconsole.exe /i \"$(args[inputFile].path)\" /al \"$(appbundles[{0}].path)\"",
                    extension = "rvt",
                    script = string.Empty
                };
            }

            throw new ArgumentException("Invalid engine");
        }
    }

    /// <summary>
    /// Input for StartWorkitem
    /// </summary>
    public class StartWorkitemInput
    {
        public IFormFile inputFile { get; set; }
        public string data { get; set; }
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
