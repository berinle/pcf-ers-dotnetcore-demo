using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.CloudFoundry;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using Octokit;
using Serilog;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Docker.DockerTasks;
using static Nuke.Common.Tools.CloudFoundry.CloudFoundryTasks;
// ReSharper disable TemplateIsNotCompileTimeConstantProblem


[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
//[AzureDevopsConfigurationGenerator(
//    VcsTriggeredTargets = new[]{"Pack"}
//)]
class Build : NukeBuild
{
    static Build()
    {
        Environment.SetEnvironmentVariable("NUKE_TELEMETRY_OPTOUT","true");
    }
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>();

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    string Framework => "net6.0";
    [Parameter("GitHub personal access token with access to the repo")]
    string GitHubToken;

    [Solution] readonly Solution Solution;

    [GitRepository] GitRepository GitRepository;
//    [GitVersion] public GitVersion GitVersion { get; set; }

    [GitVersion(Framework = "net6.0")] readonly GitVersion GitVersion;
    [Parameter("Cloud Foundry Username")]
    readonly string CfUsername;
    [Parameter("Cloud Foundry Password")]
    readonly string CfPassword;
    [Parameter("Cloud Foundry Endpoint")]
    readonly string CfApiEndpoint;
    [Parameter("Cloud Foundry Org")]
    readonly string CfOrg;
    [Parameter("Cloud Foundry Space")]
    readonly string CfSpace;
    [Parameter("Number of apps (for distributed tracing)")]
    readonly int AppsCount = 3;    
    [Parameter("Type of database plan (default: db-small)")]
    readonly string DbPlan = "db-small";
    [Parameter("Number of users to create")]
    readonly int NumUsers = 1;

    [Parameter("Skip logging in Cloud Foundry and use the current logged in session")] 
    readonly bool CfSkipLogin;

    string Runtime = "linux-x64";
    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath PublishDirectory => RootDirectory / "src" / "bin" / Configuration / Framework / Runtime  / "publish";
    string PackageZipName => $"articulate-{GitVersion.MajorMinorPatch}.zip";

    
    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .EnableNoRestore());
        });

    Target Publish => _ => _
        .Description("Publishes the project to a folder which is ready to be deployed to target machines")
        .Executes(() =>
        {
            DotNetPublish(s => s
                .SetProject(Solution)
                .SetConfiguration(Configuration)
                .SetRuntime("linux-x64")
                .DisableSelfContained()
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion));
        });

    Target Pack => _ => _
        .DependsOn(Publish)
        .Description("Publishes the project and creates a zip package in artifacts folder")
        .Produces(ArtifactsDirectory)
        .Executes(() =>
        {
            Directory.CreateDirectory(ArtifactsDirectory);
            DeleteFile(ArtifactsDirectory / PackageZipName);
            ZipFile.CreateFromDirectory(PublishDirectory, ArtifactsDirectory / PackageZipName);
            Log.Information(ArtifactsDirectory / PackageZipName);
        });

    Target CfLogin => _ => _
        .OnlyWhenStatic(() => !CfSkipLogin)
        .Requires(() => CfUsername, () => CfPassword, () => CfApiEndpoint)
        .Unlisted()
        .Executes(() =>
        {
            CloudFoundryApi(c => c.SetUrl(CfApiEndpoint));
            CloudFoundryAuth(c => c
                .SetUsername(CfUsername)
                .SetPassword(CfPassword));
        });

    Target CfCreateWorkshopUsers => _ => _
        .Executes(() =>
        {
            Dictionary<string, string> users = new();
            for (var i = 1; i < NumUsers+1; i++)
            {
                var (userName, password) = GetCredentialsForUser(i);
                CloudFoundry($"create-user {userName} {password}");
                CloudFoundry($"create-org {userName}");
                CloudFoundry($"create-org {userName} {userName} OrgManager");
                users.Add(userName, password);
            }

            Log.Information(string.Join("\n", users.Select(x => $"{x.Key} / {x.Value}")));
        });

    Target CfDeleteWorkshopUsers => _ => _
        .Executes(() =>
        {
            for (int i = 1;; i++)
            {
                var (userName, password) = GetCredentialsForUser(i);
                var results = CloudFoundry($"delete-user {userName} -f");
                if (results.Any(x => x.Text.Contains("does not exist")))
                    break;
                CloudFoundry($"delete-org {userName} -f");
            }
        });

    (string userName, string password) GetCredentialsForUser(int i) => ($"user-{i}", "p@ssword1");
    
    Target Deploy => _ => _
        .DependsOn(CfLogin)
        .After(Pack)
        .Requires(() => CfSpace, () => CfOrg)
        .Description("Deploys to Cloud Foundry")
        .Executes(async () =>
        {
            string appName = "ers1";
            
            var names = Enumerable.Range(1, AppsCount).Select(x => $"ers{x}").ToArray();;
            CloudFoundryCreateSpace(c => c
                .SetOrg(CfOrg)
                .SetSpace(CfSpace));
            CloudFoundryTarget(c => c
                .SetSpace(CfSpace)
                .SetOrg(CfOrg));
            CloudFoundryCreateService(c => c
                .SetService("p.service-registry")
                .SetPlan("standard")
                .SetInstanceName("eureka"));
            CloudFoundryCreateService(c => c
                .SetService("p.mysql")
                .SetPlan(DbPlan)
                .SetInstanceName("mysql"));
            CloudFoundryPush(c => c
                .SetRandomRoute(true)
                .SetPath(ArtifactsDirectory / PackageZipName)
                .CombineWith(names,(cs,v) => cs.SetAppName(v)), degreeOfParallelism: 1);
            await CloudFoundryEnsureServiceReady("eureka");
            await CloudFoundryEnsureServiceReady("mysql");
            CloudFoundryBindService(c => c
                .SetServiceInstance("eureka")
                .CombineWith(names,(cs,v) => cs.SetAppName(v)), degreeOfParallelism: 5);
            CloudFoundryBindService(c => c
                .SetServiceInstance("mysql")
                .CombineWith(names,(cs,v) => cs.SetAppName(v)), degreeOfParallelism: 5);
            CloudFoundryRestart(c => c
                .SetAppName(appName)
                .CombineWith(names,(cs,v) => cs.SetAppName(v)), degreeOfParallelism: 5);
        });

    Target RunSpringBootAdmin => _ => _
        .Executes(async () =>
        {
            var containerName = "spring-boot-admin";
            IReadOnlyCollection<Output> output = new Output[0];
            await Task.WhenAny(Task.Run(() =>
                output = DockerRun(c => c
                    .SetImage("steeltoeoss/spring-boot-admin")
                    .EnableRm()
                    .SetName(containerName)
                    .SetAttach("STDOUT", "STDERR")
                    .SetPublish("9090:8080"))
            ), Task.Delay(TimeSpan.FromSeconds(10)));
            
            output.EnsureOnlyStd();
            Log.Information("Press ENTER to shutdown...");
            Console.ReadLine();
            DockerKill(c => c
                .SetContainers(containerName));
        });

    Target Release => _ => _
        .Description("Creates a GitHub release (or ammends existing) and uploads the artifact")
        .DependsOn(Publish)
        .Requires(() => GitHubToken)
        .Executes(async () =>
        {
            if (!GitRepository.IsGitHubRepository())
                Assert.Fail("Only supported when git repo remote is github");
            if(!IsGitPushedToRemote)
                Assert.Fail("Your local git repo has not been pushed to remote. Can't create release until source is upload");
            var client = new GitHubClient(new ProductHeaderValue("nuke-build"))
            {
                Credentials = new Credentials(GitHubToken, AuthenticationType.Bearer)
            };
            var gitIdParts = GitRepository.Identifier.Split("/");
            var owner = gitIdParts[0];
            var repoName = gitIdParts[1];
            
            var releaseName = $"v{GitVersion.MajorMinorPatch}";
            Release release;
            try
            {
                release = await client.Repository.Release.Get(owner, repoName, releaseName);
            }
            catch (NotFoundException)
            {
                var newRelease = new NewRelease(releaseName)
                {
                    Name = releaseName, 
                    Draft = false, 
                    Prerelease = false
                };
                release = await client.Repository.Release.Create(owner, repoName, newRelease);
            }

            var existingAsset = release.Assets.FirstOrDefault(x => x.Name == PackageZipName);
            if (existingAsset != null)
            {
                await client.Repository.Release.DeleteAsset(owner, repoName, existingAsset.Id);
            }
            
            var zipPackageLocation = ArtifactsDirectory / PackageZipName;
            var releaseAssetUpload = new ReleaseAssetUpload(PackageZipName, "application/zip", File.OpenRead(zipPackageLocation), null);
            var releaseAsset = await client.Repository.Release.UploadAsset(release, releaseAssetUpload);
            
            Log.Information(releaseAsset.BrowserDownloadUrl);
        });
    
    
    
    bool IsGitPushedToRemote => GitTasks
        .Git("status")
        .Select(x => x.Text)
        .Count(x => x.Contains("nothing to commit, working tree clean") || x.StartsWith("Your branch is up to date with")) == 2;
}
