﻿using Azure.Identity;
using Azure.Storage.Blobs;
using Implem.DefinitionAccessor;
using Implem.Libraries.Utilities;
using Implem.Pleasanter.Libraries.BackgroundServices;
using Implem.Pleasanter.Libraries.DataSources;
using Implem.Pleasanter.Libraries.Initializers;
using Implem.Pleasanter.Libraries.Migrators;
using Implem.Pleasanter.Libraries.Requests;
using Implem.Pleasanter.Libraries.Security;
using Implem.Pleasanter.Libraries.Server;
using Implem.Pleasanter.Models;
using Implem.PleasanterFilters;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
namespace Implem.Pleasanter.NetCore
{
    public class Startup
    {
        IConfiguration configuration;

        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            this.configuration = configuration;
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            Applications.StartTime = DateTime.Now;
            Applications.LastAccessTime = Applications.StartTime;
            Context.Init();
            var exceptions = Initializer.Initialize(
                path: env.ContentRootPath,
                assemblyVersion: Assembly.GetExecutingAssembly().GetName().Version.ToString());
            if (exceptions.Any())
            {
                var context = InitializeContext();
                exceptions.ForEach(e =>
                    new SysLogModel(
                        context: context,
                        e: e));
            }
        }

        public void ConfigureServices(IServiceCollection services)
        {
            var context = new Context(
                request: false,
                sessionStatus: false,
                sessionData: false,
                user: false,
                item: false);
            if (Parameters.Security.AccessControlAllowOrigin?.Any() == true)
            {
                services.AddCors(options =>
                {
                    options.AddDefaultPolicy(
                        builder => builder
                            .AllowAnyMethod()
                            .AllowAnyHeader()
                            .WithOrigins(Parameters.Security.AccessControlAllowOrigin.ToArray()));
                });
            }
            services.AddControllersWithViews();
            services.AddDistributedMemoryCache();
            services.AddMvc().AddSessionStateTempDataProvider();
            services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(Parameters.Session.RetentionPeriod);
            });
            var mvcBuilder = services.AddMvc(
                options =>
                {
                    options.Filters.Add(new HandleErrorExAttribute());
                    options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
                    options.Filters.Add(new CheckContextAttributes());
                    if (Parameters.Service.RequireHttps)
                    {
                        options.Filters.Add(new Microsoft.AspNetCore.Mvc.RequireHttpsAttribute());
                    }
                });
            if (Authentications.SAML())
            {
                services
                    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddCookie(o =>
                    {
                        o.LoginPath = new PathString("/users/login");
                        o.ExpireTimeSpan = TimeSpan.FromMinutes(Parameters.Session.RetentionPeriod);
                    })
                    .AddSaml2(options =>
                    {
                        Saml.SetSPOptions(options: options);
                        Saml.RegisterSamlConfiguration(
                            context: context,
                            options: options);
                    });
            }
            else
            {
                services
                    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddCookie(o =>
                    {
                        o.LoginPath = new PathString("/users/login");
                        o.ExpireTimeSpan = TimeSpan.FromMinutes(Parameters.Session.RetentionPeriod);
                    });
            }
            services.AddSingleton<ITicketStore, AuthenticationTicketStore>();
            services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
                .Configure<ITicketStore>((options, store) => options.SessionStore = store);
            if (Parameters.Security.SecureCookies)
            {
                services.Configure<CookiePolicyOptions>(options =>
                {
                    options.Secure = CookieSecurePolicy.Always;
                });
            }
            var extensionDirectory = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "ExtendedLibraries");
            if (Directory.Exists(extensionDirectory))
            {
                foreach (var assembly in Directory.GetFiles(extensionDirectory, "*.dll").Select(dll => Assembly.LoadFrom(dll)).ToArray())
                {
                    mvcBuilder.AddApplicationPart(assembly);
                }
            }
            services.Configure<FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = int.MaxValue;
            });
            services.Configure<IISServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
                options.MaxRequestBodySize = Parameters.Service.MaxRequestBodySize;
            });
            services.Configure<KestrelServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
                options.Limits.MaxRequestBodySize = Parameters.Service.MaxRequestBodySize;
            })
            .Configure<KestrelServerOptions>(configuration.GetSection("Kestrel"));
            services.AddHealthChecks();
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            });
            services.Configure<HostOptions>(options =>
            {
                options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            });
            if (Parameters.BackgroundService.ReminderEnabled(
                deploymentEnvironment: Parameters.Service.DeploymentEnvironment))
            {
                services.AddHostedService<ReminderBackgroundService>();
            }
            if (Parameters.BackgroundService.TimerEnabled(
                deploymentEnvironment: Parameters.Service.DeploymentEnvironment))
            {
                services.AddHostedService<TimerBackgroundService>();
            }
            var blobContainerUri = Parameters.Security.AspNetCoreDataProtection?.BlobContainerUri;
            var keyIdentifier = Parameters.Security.AspNetCoreDataProtection?.KeyIdentifier;
            if (!blobContainerUri.IsNullOrEmpty()
                && !keyIdentifier.IsNullOrEmpty())
            {
                var blobContainer = new BlobContainerClient(new Uri(blobContainerUri), new DefaultAzureCredential());
                blobContainer.CreateIfNotExists();
                var blobClient = blobContainer.GetBlobClient(Parameters.Security.AspNetCoreDataProtection?.KeyFileName ?? "keys.xml");
                services
                    .AddDataProtection()
                    .PersistKeysToAzureBlobStorage(blobClient)
                    .ProtectKeysWithAzureKeyVault(new Uri(keyIdentifier), new DefaultAzureCredential());
            }
            else
            {
                services
                    .AddOptions<KeyManagementOptions>()
                    .Configure<IServiceScopeFactory>((options, factory) =>
                    {
                        options.XmlRepository = new AspNetCoreKeyManagementXmlRepository();
                        options.XmlEncryptor = new AspNetCoreKeyManagementXmlEncryptor();
                    });
            }
            if (Parameters.Security.HttpStrictTransportSecurity?.Enabled == true)
            {
                services.AddHsts(options =>
                {
                    options.Preload = Parameters.Security.HttpStrictTransportSecurity.Preload;
                    options.IncludeSubDomains = Parameters.Security.HttpStrictTransportSecurity.IncludeSubDomains;
                    options.MaxAge = Parameters.Security.HttpStrictTransportSecurity.MaxAge;
                    if (Parameters.Security.HttpStrictTransportSecurity.ExcludeHosts != null)
                    {
                        foreach (var host in Parameters.Security.HttpStrictTransportSecurity.ExcludeHosts)
                        {
                            options.ExcludedHosts.Add(host);
                        }
                    }
                });
            }
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseForwardedHeaders();
            app.UseCurrentRequestContext();
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/errors/internalservererror");
            }
            app.UseHsts();
            app.UseSecurityHeadersMiddleware();
            app.Use(async (context, next) => await Invoke(context, next));
            app.UseStatusCodePages(context =>
            {
                var statusCode = context.HttpContext.Response.StatusCode;
                if (statusCode == 400) context.HttpContext.Response.Redirect("/errors/badrequest");
                else if (statusCode == 404) context.HttpContext.Response.Redirect("/errors/notfound");
                else if (statusCode == 405) context.HttpContext.Response.Redirect("/errors/badrequest");
                else if (statusCode == 500) context.HttpContext.Response.Redirect("/errors/internalservererror");
                else if (statusCode == 401
                    && !context.HttpContext.User.Identity.IsAuthenticated
                    && context.HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    context.HttpContext.Response.StatusCode = 403;
                    context.HttpContext.Response.ContentType = "application/json";
                    context.HttpContext.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Message = Libraries.Responses.Displays.Unauthorized(context: new Context())
                    }));
                }
                else context.HttpContext.Response.Redirect("/errors/internalservererror");
                return Task.CompletedTask;
            });
            app.UsePathBase(configuration["pathBase"]);
            app.UseStaticFiles();
            app.UseCookiePolicy();
            app.UseRouting();
            app.UseCors();
            app.UseSession();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseSessionMiddleware();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
                endpoints.MapControllerRoute(
                    name: "Default",
                    pattern: "{controller}/{action}",
                    defaults: new
                    {
                        Controller = "Items",
                        Action = "Index"
                    },
                    constraints: new
                    {
                        Controller = "[A-Za-z][A-Za-z0-9_]*",
                        Action = "[A-Za-z][A-Za-z0-9_]*"
                    }
                );
                endpoints.MapControllerRoute(
                    name: "Others",
                    pattern: "{reference}/{id}/{controller}/{action}",
                    defaults: new
                    {
                        Action = "Index"
                    },
                    constraints: new
                    {
                        Reference = "[A-Za-z][A-Za-z0-9_]*",
                        Id = "[0-9]+",
                        Controller = "Binaries|PublishBinaries|OutgoingMails",
                        Action = "[A-Za-z][A-Za-z0-9_]*"
                    }
                );
                endpoints.MapControllerRoute(
                    name: "Item",
                    pattern: "{controller}/{id}/{action}",
                    defaults: new
                    {
                        Controller = "Items",
                        Action = "Edit"
                    },
                    constraints: new
                    {
                        Id = "[0-9]+",
                        Action = "[A-Za-z][A-Za-z0-9_]*"
                    }
                );
                endpoints.MapControllerRoute(
                    name: "Binaries",
                    pattern: "{controller}/{guid}/{action}",
                    defaults: new
                    {
                        Controller = "Binaries"
                    },
                    constraints: new
                    {
                        Guid = "[A-Za-z0-9]+",
                        Action = "[A-Za-z][A-Za-z0-9_]*"
                    }
                );
                endpoints.MapControllerRoute(
                    name: "BinariesUpload",
                    pattern: "binaries/upload",
                    defaults: new
                    {
                        Controller = "Binaries",
                        Action = "Upload"
                    },
                    constraints: new
                    {
                        Guid = "[A-Za-z0-9]+",
                        Action = "[A-Za-z][A-Za-z0-9_]*"
                    }
                );
            });
        }

        private static Context InitializeContext()
        {
            return new Context(
                tenantId: 0,
                request: false)
            {
                Controller = "Startup.cs",
                Action = "Initialize",
                Id = 0
            };
        }

        private static Context ApplicationStartContext()
        {
            return new Context(tenantId: 0)
            {
                Controller = "Startup.cs",
                Action = "Application_Start",
                Id = 0
            };
        }

        private static bool isFirst = true;
        public async Task Invoke(HttpContext httpContext, Func<Task> next)
        {
            if (isFirst)
            {
                isFirst = false;
                Initialize();
            }
            try
            {
                await next.Invoke();
            }
            catch (Exception error)
            {
                try
                {
                    var context = new Context();
                    var log = new SysLogModel(context: context);
                    log.SysLogType = SysLogModel.SysLogTypes.Execption;
                    log.ErrMessage = error.Message;
                    log.ErrStackTrace = error.StackTrace;
                    log.Finish(context: context);
                }
                catch
                {
                    throw;
                }
                throw;
            }
        }

        private void OnShutdown()
        {
            var context = new Context();
            var log = new SysLogModel(context: context);
            log.Finish(context: context);
        }

        private void Initialize()
        {
            Context context = ApplicationStartContext();
            var log = new SysLogModel(
                context: context,
                method: null,
                message: Parameters.GetLicenseInfo().ToJson());
            TenantInitializer.Initialize();
            ExtensionInitializer.Initialize(context: context);
            UsersInitializer.Initialize(context: context);
            ItemsInitializer.Initialize(context: context);
            StatusesMigrator.Migrate(context: context);
            SiteSettingsMigrator.Migrate(context: context);
            StatusesInitializer.Initialize(context: context);
            NotificationInitializer.Initialize();
            SiteInfo.Reflesh(context: context);
            log.Finish(context: context);
        }
    }

    public class SessionMiddleware
    {
        private readonly RequestDelegate _next;

        public SessionMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext httpContext)
        {
            const string enabled = "Enabled";
            if (!httpContext.Session.Keys.Any(key => key == enabled))
            {
                AspNetCoreCurrentRequestContext.AspNetCoreHttpContext.Current.Session.Set("SessionGuid", System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(Strings.NewGuid())));
                SetClientId();
                httpContext.Session.Set(enabled, new byte[] { 1 });
                var context = SessionStartContext();
                SessionUtilities.SetStartTime(context: context);
                if (WindowsAuthenticated(context))
                {
                    Ldap.UpdateOrInsert(
                        context: context,
                        loginId: context.LoginId);
                    context.Set();
                }
                if (context.Authenticated)
                {
                    StatusesInitializer.Initialize(context: context);
                }
                switch (httpContext.Request.Path.Value.ToLower())
                {
                    case "~/backgroundtasks/do":
                    case "~/reminderschedules/remind":
                        break;
                    default:
                        break;
                }
            }
            await _next.Invoke(httpContext);
        }

        private static Context SessionStartContext()
        {
            return new Context()
            {
                Controller = "Startup.cs",
                Action = "Session_Start",
                Id = 0
            };
        }

        private static void SetClientId()
        {
            if (Parameters.SysLog.ClientId &&
                AspNetCoreCurrentRequestContext.AspNetCoreHttpContext.Current?.Request.Cookies["Pleasanter_ClientId"] == null)
            {
                AspNetCoreCurrentRequestContext.AspNetCoreHttpContext.Current?.Response.Cookies.Append(
                    "Pleasanter_ClientId",
                    Strings.NewGuid(),
                    new CookieOptions()
                    {
                        Expires = DateTime.UtcNow.AddDays(400),
                        Secure= true
                    });
            }
        }

        private static bool WindowsAuthenticated(Context context)
        {
            return Authentications.Windows(context: context)
                && !context.LoginId.IsNullOrEmpty()
                && (!Parameters.Authentication.RejectUnregisteredUser
                || context.Authenticated);
        }
    }

    public static class SessionMiddlewareExtensions
    {
        public static IApplicationBuilder UseSessionMiddleware(this IApplicationBuilder app)
        {
            return app.UseMiddleware<SessionMiddleware>();
        }
    }

    public class SecurityHeadersMiddleware
    {
        private readonly RequestDelegate _next;

        public SecurityHeadersMiddleware(RequestDelegate next)
        {
            _next = next;
        }
        public Task Invoke(HttpContext context)
        {
            context.Response.Headers.Add("X-Frame-Options", new StringValues("SAMEORIGIN"));
            context.Response.Headers.Add("X-Xss-Protection", new StringValues("1; mode=block"));
            context.Response.Headers.Add("X-Content-Type-Options", new StringValues("nosniff"));
            if (Parameters.Security.SecureCacheControl != null)
            {
                if (Parameters.Security.SecureCacheControl.NoCache
                    || Parameters.Security.SecureCacheControl.NoStore
                    || Parameters.Security.SecureCacheControl.Private
                    || Parameters.Security.SecureCacheControl.MustRevalidate)
                {
                    context.Response.GetTypedHeaders().CacheControl =
                        new Microsoft.Net.Http.Headers.CacheControlHeaderValue()
                        {
                            NoCache = Parameters.Security.SecureCacheControl.NoCache,
                            NoStore = Parameters.Security.SecureCacheControl.NoStore,
                            Private = Parameters.Security.SecureCacheControl.Private,
                            MustRevalidate = Parameters.Security.SecureCacheControl.MustRevalidate
                        };
                }
                if (Parameters.Security.SecureCacheControl.PragmaNoCache)
                {
                    context.Response.Headers.Add("Pragma", new StringValues("no-cache"));
                }
            }
            return _next(context);
        }
    }

    public static class SecurityHeadersMiddlewareExtensions
    {
        public static IApplicationBuilder UseSecurityHeadersMiddleware(this IApplicationBuilder app)
        {
            return app.UseMiddleware<SecurityHeadersMiddleware>();
        }
    }
}
