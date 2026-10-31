using System;
using System.Linq;
using Cntryl.Portia;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddPortia().AddCassie("Host=localhost;Port=5432;Database=postgres;Username=root;Password=unused");
if (!services.Any(descriptor => descriptor.ServiceType == typeof(CassieProjectionDatabase)))
    throw new InvalidOperationException("Cassie projection metadata was not registered.");
