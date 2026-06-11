using FFXIV_Craft_Architect.ViewModels;

namespace FFXIV_Craft_Architect.Tests;

public class ViewModelBaseTests
{
    [Fact]
    public void SetProperty_WhenValueChanges_UpdatesValueAndRaisesPropertyChanged()
    {
        var viewModel = new TestViewModel();
        var propertyChanges = new List<string?>();
        viewModel.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        viewModel.Name = "New Name";

        Assert.Equal("New Name", viewModel.Name);
        Assert.Equal(["Name"], propertyChanges);
    }

    [Fact]
    public void SetProperty_WhenValueIsUnchanged_DoesNotRaisePropertyChanged()
    {
        var viewModel = new TestViewModel { Name = "Initial" };
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;

        viewModel.Name = "Initial";

        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public void Dispose_CallsVirtualDisposeOnlyOnce()
    {
        var viewModel = new TestViewModel();

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.Equal(1, viewModel.DisposeCallCount);
    }

    [Fact]
    public async Task SafeFireAndForget_CompletedTask_DoesNotCallErrorHandler()
    {
        var viewModel = new TestViewModel();
        Exception? capturedException = null;

        viewModel.TestSafeFireAndForget(Task.CompletedTask, ex => capturedException = ex);
        await Task.Delay(50);

        Assert.Null(capturedException);
    }

    [Fact]
    public async Task SafeFireAndForget_FaultedTask_WithErrorHandler_CallsHandler()
    {
        var viewModel = new TestViewModel();
        var expectedException = new InvalidOperationException("Test exception");
        Exception? capturedException = null;
        var handled = new TaskCompletionSource();

        viewModel.TestSafeFireAndForget(Task.FromException(expectedException), ex =>
        {
            capturedException = ex;
            handled.SetResult();
        });
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(expectedException, capturedException);
    }

    [Fact]
    public async Task SafeFireAndForget_FaultedTask_WithoutErrorHandler_DoesNotThrow()
    {
        var viewModel = new TestViewModel();

        viewModel.TestSafeFireAndForget(Task.FromException(new InvalidOperationException("Test exception")));
        await Task.Delay(50);
    }

    private sealed class TestViewModel : ViewModelBase
    {
        private string _name = string.Empty;

        public int DisposeCallCount { get; private set; }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public void TestSafeFireAndForget(Task task, Action<Exception>? onError = null)
        {
            SafeFireAndForget(task, onError);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCallCount++;
            }

            base.Dispose(disposing);
        }
    }
}
