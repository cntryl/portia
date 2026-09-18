namespace Cntryl.Portia;

interface ITestProjectionRepository : IProjectionStore
{
    TestProjection Projection { get; }
}
