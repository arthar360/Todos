﻿using System;
using System.Linq;
using System.IO;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Funq;
using ServiceStack;
using ServiceStack.Configuration;
using ServiceStack.Host.Handlers;
using ServiceStack.Redis;
using ServiceStack.VirtualPath;
using System.Text;

namespace Todos
{
    public class Program
    {
        public static void Main(string[] args)
        {
            BuildWebHost(args).Run();
        }

        public static IWebHost BuildWebHost(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .UseUrls("http://0.0.0.0:5000/")
                .Build();
    }

    public class Startup
    {
        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            loggerFactory.AddConsole();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // only gets run if SS doesn't handle the request, i.e. can't find the file:
            app.Use(async (context, next) => {
                var virtualPath = context.Request.Path.Value;

                if (context.Request.Query.ContainsKey("debugOn"))
                    HostContext.Config.DebugMode = true;
                if (context.Request.Query.ContainsKey("debugOff"))
                    HostContext.Config.DebugMode = false;
                
                if (context.Request.Query.ContainsKey("info"))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("VirtualFiles RealPath: " + HostContext.VirtualFiles.RootDirectory.RealPath);
                    sb.AppendLine("VirtualFileSources RealPath: " + HostContext.VirtualFileSources.RootDirectory.RealPath);
                    sb.AppendLine("FileExists: " + HostContext.VirtualFileSources.FileExists(virtualPath));
                    sb.AppendLine("DirectoryExists: " + HostContext.VirtualFileSources.DirectoryExists(virtualPath));
                    sb.AppendLine("WebHostPhysicalPath: " + HttpHandlerFactory.WebHostPhysicalPath);
                    sb.AppendLine("WebHostRootFileNames: " + HttpHandlerFactory.WebHostRootFileNames.ToJsv());

                    var bytes = sb.ToString().ToUtf8Bytes();
                    context.Response.Body.Write(bytes, 0, bytes.Length);
                    context.Response.Body.Close();
                    return;
                }

                if (context.Request.Query.ContainsKey("next"))
                {
                    await next();
                    return;                    
                }

                var file = HostContext.AppHost.VirtualFileSources.GetFile(virtualPath);

                var vfs = (MultiVirtualFiles)HostContext.AppHost.VirtualFileSources;
                var physicalPath = vfs.CombineVirtualPath(vfs.RootDirectory.RealPath, virtualPath);

                if (file != null)
                {
                    await new StaticFileHandler().Middleware(context, () => null);
                }
                else
                {
                    var str = $"No file found at '{virtualPath}' using MultiVirtualFiles:\n";
                    str += $"physicalPath: {physicalPath}\n";
                    str += $"ResolvePhysicalPath: {HostContext.ResolvePhysicalPath(virtualPath, null)}\n";
                    str += $"{vfs.GetType().Name} = real: {vfs.RootDirectory.RealPath}, virtual: {vfs.RootDirectory.VirtualPath}\n";

                    foreach (var childVfs in vfs.ChildProviders)
                    {
                        file = childVfs.GetFile(virtualPath);
                        str += $"file using {childVfs.GetType().Name} at {childVfs.RootDirectory.RealPath} = real: {file?.RealPath}, virtual: {file?.VirtualPath}\n";
                    }

                    var bytes = str.ToUtf8Bytes();
                    context.Response.Body.Write(bytes, 0, bytes.Length);
                }
                context.Response.Body.Close();
            });

            app.UseServiceStack(new AppHost());
        }
    }

    // Create your ServiceStack Web Service with a singleton AppHost
    public class AppHost : AppHostBase
    {
        // Initializes your AppHost Instance, with the Service Name and assembly containing the Services
        public AppHost() : base("Backbone.js TODO", typeof(TodoService).GetAssembly()) 
        { 
            AppSettings = new MultiAppSettings(
                new EnvironmentVariableSettings(),
                new AppSettings());
        }

        // Configure your AppHost with the necessary configuration and dependencies your App needs
        public override void Configure(Container container)
        {
            //Register Redis Client Manager singleton in ServiceStack's built-in Func IOC
            container.Register<IRedisClientsManager>(c =>
                new RedisManagerPool(AppSettings.Get("REDIS_HOST", defaultValue:"localhost")));
        }
    }

    // Define your ServiceStack web service request (i.e. Request DTO).
    [Route("/todos")]
    [Route("/todos/{Id}")]
    public class Todo
    {
        public long Id { get; set; }
        public string Content { get; set; }
        public int Order { get; set; }
        public bool Done { get; set; }
    }

    // Create your ServiceStack rest-ful web service implementation. 
    public class TodoService : Service
    {
        public object Get(Todo todo)
        {
            //Return a single Todo if the id is provided.
            if (todo.Id != default(long))
                return Redis.As<Todo>().GetById(todo.Id);

            //Return all Todos items.
            return Redis.As<Todo>().GetAll();
        }

        // Handles creating and updating the Todo items.
        public Todo Post(Todo todo)
        {
            var redis = Redis.As<Todo>();

            //Get next id for new todo
            if (todo.Id == default(long))
                todo.Id = redis.GetNextSequence();

            redis.Store(todo);

            return todo;
        }

        // Handles creating and updating the Todo items.
        public Todo Put(Todo todo)
        {
            return Post(todo);
        }

        // Handles Deleting the Todo item
        public void Delete(Todo todo)
        {
            Redis.As<Todo>().DeleteById(todo.Id);
        }
    }
}
