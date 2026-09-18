namespace Cntryl.Portia;

sealed record PortiaHttpContractParameter(string Name, Type Type, PortiaHttpParameterLocation Location, bool Required);
