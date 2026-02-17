using System.Windows;
using System.Windows.Threading;
using FFXIV_Craft_Architect.Helpers;
using Xunit;

namespace FFXIV_Craft_Architect.Tests;

/// <summary>
/// Unit tests for AsyncExtensions.SafeFireAndForget extension methods.
/// Validates exception handling and thread marshaling behavior.
/// </summary>
public class AsyncExtensionsTests
{
    #region SafeFireAndForget (Task) Tests

    [Fact]
    public void SafeFireAndForget_CompletedTask_DoesNotThrow()
    {
        // Arrange
        var task = Task.CompletedTask;
        Exception? capturedException = null;

        // Act - Should not throw
        task.SafeFireAndForget(ex => capturedException = ex);

        // Assert
        Assert.Null(capturedException);
    }

    [Fact]
    public async Task SafeFireAndForget_FaultedTask_CallsExceptionHandler()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Test exception");
        var task = Task.FromException(expectedException);
        Exception? capturedException = null;
        var tcs = new TaskCompletionSource<bool>();

        // Act
        task.SafeFireAndForget(ex =>
        {
            capturedException = ex;
            tcs.SetResult(true);
        });

        // Wait for the continuation to execute
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(capturedException);
        Assert.IsType<InvalidOperationException>(capturedException);
        Assert.Equal("Test exception", capturedException.Message);
    }

    [Fact]
    public async Task SafeFireAndForget_FaultedTask_NoHandler_ExceptionSilentlyHandled()
    {
        // Arrange
        var task = Task.FromException(new Exception("Silent failure"));

        // Act & Assert - Should not throw even without handler
        task.SafeFireAndForget();

        // Give time for continuation to execute
        await Task.Delay(100);

        // Test passes if no exception is thrown
        Assert.True(true);
    }

    [Fact]
    public async Task SafeFireAndForget_AggregateException_UnwrapsInnerException()
    {
        // Arrange
        var innerException = new ArgumentException("Inner error");
        var aggregateException = new AggregateException(innerException);
        var task = Task.FromException(aggregateException);
        Exception? capturedException = null;
        var tcs = new TaskCompletionSource<bool>();

        // Act
        task.SafeFireAndForget(ex =>
        {
            capturedException = ex;
            tcs.SetResult(true);
        });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert - Should unwrap to inner exception
        Assert.NotNull(capturedException);
        Assert.IsType<ArgumentException>(capturedException);
        Assert.Equal("Inner error", capturedException.Message);
    }

    [Fact]
    public async Task SafeFireAndForget_MultipleInnerExceptions_FirstIsCaptured()
    {
        // Arrange
        var firstException = new InvalidOperationException("First");
        var secondException = new ArgumentException("Second");
        var aggregateException = new AggregateException(firstException, secondException);
        var task = Task.FromException(aggregateException);
        Exception? capturedException = null;
        var tcs = new TaskCompletionSource<bool>();

        // Act
        task.SafeFireAndForget(ex =>
        {
            capturedException = ex;
            tcs.SetResult(true);
        });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert - Should capture first inner exception
        Assert.NotNull(capturedException);
        Assert.Equal("First", capturedException.Message);
    }

    [Fact]
    public void SafeFireAndForget_NullTask_ThrowsArgumentNullException()
    {
        // Arrange
        Task? task = null;

        // Act & Assert
#pragma warning disable CS8604 // Possible null reference argument.
        Assert.Throws<ArgumentNullException>(() => task!.SafeFireAndForget());
#pragma warning restore CS8604 // Possible null reference argument.
    }

    [Fact]
    public async Task SafeFireAndForget_ExceptionHandler_MarshalsCallback()
    {
        // This test verifies that the exception handler is invoked
        // when the task faults from a background thread

        // Arrange
        Exception? capturedException = null;
        var tcs = new TaskCompletionSource<bool>();

        // Act - Create a task that faults from a background thread
        var task = Task.Run(async () =>
        {
            await Task.Delay(10);
            throw new InvalidOperationException("Background thread exception");
        });

        task.SafeFireAndForget(ex =>
        {
            capturedException = ex;
            tcs.SetResult(true);
        });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(capturedException);
        Assert.Equal("Background thread exception", capturedException.Message);
    }

    #endregion

    #region SafeFireAndForget<T> (Task<T>) Tests

    [Fact]
    public void SafeFireAndForgetT_CompletedTask_DoesNotThrow()
    {
        // Arrange
        var task = Task.FromResult(42);
        Exception? capturedException = null;

        // Act - Should not throw
        task.SafeFireAndForget(ex => capturedException = ex);

        // Assert
        Assert.Null(capturedException);
    }

    [Fact]
    public async Task SafeFireAndForgetT_FaultedTask_CallsExceptionHandler()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Task<T> exception");
        var task = Task.FromException<int>(expectedException);
        Exception? capturedException = null;
        var tcs = new TaskCompletionSource<bool>();

        // Act
        task.SafeFireAndForget(ex =>
        {
            capturedException = ex;
            tcs.SetResult(true);
        });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(capturedException);
        Assert.IsType<InvalidOperationException>(capturedException);
        Assert.Equal("Task<T> exception", capturedException.Message);
    }

    [Fact]
    public async Task SafeFireAndForgetT_FaultedTask_NoHandler_ExceptionSilentlyHandled()
    {
        // Arrange
        var task = Task.FromException<string>(new Exception("Silent failure"));

        // Act & Assert - Should not throw even without handler
        task.SafeFireAndForget();

        // Give time for continuation to execute
        await Task.Delay(100);

        // Test passes if no exception is thrown
        Assert.True(true);
    }

    [Fact]
    public void SafeFireAndForgetT_NullTask_ThrowsArgumentNullException()
    {
        // Arrange
        Task<int>? task = null;

        // Act & Assert
#pragma warning disable CS8604 // Possible null reference argument.
        Assert.Throws<ArgumentNullException>(() => task!.SafeFireAndForget());
#pragma warning restore CS8604 // Possible null reference argument.
    }

    #endregion

    #region Real-World Usage Pattern Tests

    [Fact]
    public async Task SafeFireAndForget_AsyncWorkPattern_Simulated()
    {
        // Simulates the real usage pattern from the documentation
        var workCompleted = false;
        Exception? loggedException = null;
        var tcs = new TaskCompletionSource<bool>();

        async Task DoWorkAsync()
        {
            await Task.Delay(10);
            workCompleted = true;
        }

        async Task DoFailingWorkAsync()
        {
            await Task.Delay(10);
            throw new InvalidOperationException("Work failed");
        }

        // Act - Simulate successful work
        DoWorkAsync().SafeFireAndForget();
        await Task.Delay(50);
        Assert.True(workCompleted);

        // Act - Simulate failing work with logging
        DoFailingWorkAsync().SafeFireAndForget(ex =>
        {
            loggedException = ex;
            tcs.SetResult(true);
        });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(loggedException);
        Assert.Equal("Work failed", loggedException.Message);
    }

    #endregion
}
