using System.Threading.Tasks;
using Cntryl.Cassie.Connection;
using System;
using System.Linq;
using Cntryl.Portia;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddPortia().AddCassie("Host=localhost;Port=5432;Database=postgres;Username=root;Password=unused");
if (!services.Any(descriptor => descriptor.ServiceType == typeof(CassieProjectionDatabase)))
    throw new InvalidOperationException("Cassie projection metadata was not registered.");

const string passwordless = "Host=localhost;Port=5432;Database=postgres;Username=cassie_aws_iam";
var iamServices = new ServiceCollection();
iamServices.AddPortia().AddCassie(static (_, _) => ValueTask.FromResult("unused-token"), passwordless);
if (!iamServices.Any(descriptor => descriptor.ServiceType == typeof(CassieDataSource)))
    throw new InvalidOperationException("Cassie IAM settings were not registered.");
