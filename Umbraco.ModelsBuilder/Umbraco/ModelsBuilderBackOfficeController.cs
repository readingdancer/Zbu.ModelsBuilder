using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Hosting;
using Umbraco.Core.Configuration;
using Umbraco.ModelsBuilder.Building;
using Umbraco.ModelsBuilder.Configuration;
using Umbraco.ModelsBuilder.Dashboard;
using Umbraco.Web.Editors;

namespace Umbraco.ModelsBuilder.Umbraco
{
    /// <summary>
    /// API controller for use in the Umbraco back office with Angular resources
    /// </summary>
    /// <remarks>
    /// We've created a different controller for the backoffice/angular specifically this is to ensure that the
    /// correct CSRF security is adhered to for angular and it also ensures that this controller is not subseptipal to
    /// global WebApi formatters being changed since this is always forced to only return Angular JSON Specific formats.
    /// </remarks>
    public class ModelsBuilderBackOfficeController : UmbracoAuthorizedJsonController
    {
        // invoked by the dashboard
        // requires that the user is logged into the backoffice and has access to the developer section
        // beware! the name of the method appears in modelsbuilder.controller.js
        [System.Web.Http.HttpPost] // use the http one, not mvc, with api controllers!
        public HttpResponseMessage BuildModels()
        {
            try
            {
                if (!UmbracoConfig.For.ModelsBuilder().ModelsMode.SupportsExplicitGeneration())
                {
                    var result2 = new BuildResult { Success = false, Message = "Models generation is not enabled." };
                    return Request.CreateResponse(HttpStatusCode.OK, result2, Configuration.Formatters.JsonFormatter);
                }

                var modelsDirectory = UmbracoConfig.For.ModelsBuilder().ModelsDirectory;
                var binPath = UmbracoConfig.For.ModelsBuilder().BinDirectory;

                if (string.IsNullOrWhiteSpace(binPath))
                {
                    binPath = HostingEnvironment.MapPath("~/bin");
                    if (binPath == null)
                        throw new Exception("Panic: bin is null.");
                }

                // EnableDllModels will recycle the app domain - but this request will end properly
                GenerateModels(modelsDirectory, UmbracoConfig.For.ModelsBuilder().ModelsMode.IsAnyDll() ? binPath : null);

                ModelsGenerationError.Clear();
            }
            catch (Exception e)
            {
                ModelsGenerationError.Report("Failed to build models.", e);
            }

            return Request.CreateResponse(HttpStatusCode.OK, GetDashboardResult(), Configuration.Formatters.JsonFormatter);
        }

        // invoked by the back-office
        // requires that the user is logged into the backoffice and has access to the developer section
        [System.Web.Http.HttpGet] // use the http one, not mvc, with api controllers!
        public HttpResponseMessage GetModelsOutOfDateStatus()
        {
            var status = OutOfDateModelsStatus.IsEnabled
                ? (OutOfDateModelsStatus.IsOutOfDate
                    ? new OutOfDateStatus { Status = OutOfDateType.OutOfDate }
                    : new OutOfDateStatus { Status = OutOfDateType.Current })
                : new OutOfDateStatus { Status = OutOfDateType.Unknown };

            return Request.CreateResponse(HttpStatusCode.OK, status, Configuration.Formatters.JsonFormatter);
        }

        // invoked by the back-office
        // requires that the user is logged into the backoffice and has access to the developer section
        // beware! the name of the method appears in modelsbuilder.controller.js
        [System.Web.Http.HttpGet] // use the http one, not mvc, with api controllers!
        public HttpResponseMessage GetDashboard()
        {
            return Request.CreateResponse(HttpStatusCode.OK, GetDashboardResult(), Configuration.Formatters.JsonFormatter);
        }

        private Dashboard GetDashboardResult()
        {
            return new Dashboard
            {
                Enable = UmbracoConfig.For.ModelsBuilder().Enable,
                Text = ModelsBuilder.Dashboard.DashboardHelper.Text(),
                CanGenerate = ModelsBuilder.Dashboard.DashboardHelper.CanGenerate(),
                GenerateCausesRestart = ModelsBuilder.Dashboard.DashboardHelper.GenerateCausesRestart(),
                OutOfDateModels = ModelsBuilder.Dashboard.DashboardHelper.AreModelsOutOfDate(),
                LastError = ModelsBuilder.Dashboard.DashboardHelper.LastError(),
            };
        }

        internal static void GenerateModels(string modelsDirectory, string binPath)
        {
            if (!Directory.Exists(modelsDirectory))
                Directory.CreateDirectory(modelsDirectory);

            foreach (var file in Directory.GetFiles(modelsDirectory, "*.generated.cs"))
                File.Delete(file);

            var umbraco = Application.GetApplication();
            var typeModels = umbraco.GetAllTypes();

            var ourFiles = Directory.GetFiles(modelsDirectory, "*.cs").ToDictionary(x => x, File.ReadAllText);
            var parseResult = new CodeParser().ParseWithReferencedAssemblies(ourFiles);
            var builder = new TextBuilder(typeModels, parseResult, UmbracoConfig.For.ModelsBuilder().ModelsNamespace);

            foreach (var typeModel in builder.GetModelsToGenerate())
            {
                var sb = new StringBuilder();
                builder.Generate(sb, typeModel);
                var filename = Path.Combine(modelsDirectory, typeModel.ClrName + ".generated.cs");
                File.WriteAllText(filename, sb.ToString());
            }

            // the idea was to calculate the current hash and to add it as an extra file to the compilation,
            // in order to be able to detect whether a DLL is consistent with an environment - however the
            // environment *might not* contain the local partial files, and thus it could be impossible to
            // calculate the hash. So... maybe that's not a good idea after all?
            /*
            var currentHash = HashHelper.Hash(ourFiles, typeModels);
            ourFiles["models.hash.cs"] = $@"using Umbraco.ModelsBuilder;
[assembly:ModelsBuilderAssembly(SourceHash = ""{currentHash}"")]
";
            */

            if (binPath != null)
            {
                //When bin directory is changed by config
                if (!Directory.Exists(binPath))
                    Directory.CreateDirectory(binPath);

                foreach (var file in Directory.GetFiles(modelsDirectory, "*.generated.cs"))
                    ourFiles[file] = File.ReadAllText(file);
                var compiler = new Compiler();
                compiler.Compile(builder.GetModelsNamespace(), ourFiles, binPath);
            }

            OutOfDateModelsStatus.Clear();
        }

        [DataContract]
        internal class BuildResult
        {
            [DataMember(Name = "success")]
            public bool Success;
            [DataMember(Name = "message")]
            public string Message;
        }

        [DataContract]
        internal class Dashboard
        {
            [DataMember(Name = "enable")]
            public bool Enable;
            [DataMember(Name = "text")]
            public string Text;
            [DataMember(Name = "canGenerate")]
            public bool CanGenerate;
            [DataMember(Name = "generateCausesRestart")]
            public bool GenerateCausesRestart;
            [DataMember(Name = "outOfDateModels")]
            public bool OutOfDateModels;
            [DataMember(Name = "lastError")]
            public string LastError;
        }

        internal enum OutOfDateType
        {
            OutOfDate,
            Current,
            Unknown = 100
        }

        [DataContract]
        internal class OutOfDateStatus
        {
            [DataMember(Name = "status")]
            public OutOfDateType Status { get; set; }
        }
    }
}