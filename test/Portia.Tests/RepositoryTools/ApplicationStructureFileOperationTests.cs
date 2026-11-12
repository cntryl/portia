using System;
using System.Collections.Generic;
using System.IO;
using Cntryl.Portia.RepositoryTools;
using Xunit;

namespace Cntryl.Portia.Tests.RepositoryTools;

/// <summary>File-operation path allocation preserves files created by concurrent processes.</summary>
public sealed class ApplicationStructureFileOperationTests
{
    /// <summary>Only a file successfully opened with create-new is registered as staged.</summary>
    [Fact]
    public void ShouldRegisterOnlyTheFileCreatedWithCreateNew()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var collisionPath = Path.Combine(directory, "collision.stage");
            var createdPath = Path.Combine(directory, "created.stage");
            File.WriteAllText(collisionPath, "owned by another process");
            var candidates = new Queue<string>([collisionPath, createdPath]);
            var registeredPaths = new List<string>();

            using (var stream = ApplicationStructureCommand.CreateStagedFile(candidates.Dequeue, registeredPaths.Add))
                stream.Write([0x2A]);

            Assert.Equal([createdPath], registeredPaths);
            Assert.Equal("owned by another process", File.ReadAllText(collisionPath));
            Assert.Equal(new byte[] { 0x2A }, File.ReadAllBytes(createdPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A colliding backup destination remains owned by its original creator.</summary>
    [Fact]
    public void ShouldMoveToAnotherBackupPathWhenAConcurrentCollisionAppears()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sourcePath = Path.Combine(directory, "source.cs");
            var collisionPath = Path.Combine(directory, "collision.backup");
            var backupPath = Path.Combine(directory, "created.backup");
            File.WriteAllText(sourcePath, "original source");
            File.WriteAllText(collisionPath, "owned by another process");
            var candidates = new Queue<string>([collisionPath, backupPath]);

            var movedPath = ApplicationStructureCommand.MoveToUniqueBackup(sourcePath, candidates.Dequeue);

            Assert.Equal(backupPath, movedPath);
            Assert.False(File.Exists(sourcePath));
            Assert.Equal("original source", File.ReadAllText(backupPath));
            Assert.Equal("owned by another process", File.ReadAllText(collisionPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "portia-file-operation-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
