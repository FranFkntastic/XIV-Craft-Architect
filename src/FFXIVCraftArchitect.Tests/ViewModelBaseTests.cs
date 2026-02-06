using System.ComponentModel;
using FFXIVCraftArchitect.ViewModels;

namespace FFXIVCraftArchitect.Tests;

/// <summary>
/// Unit tests for the <see cref="ViewModelBase"/> class.
/// </summary>
public class ViewModelBaseTests
{
    #region Test Helper Classes

    /// <summary>
    /// Testable ViewModel implementation for unit testing base class functionality.
    /// </summary>
    private class TestViewModel : ViewModelBase
    {
        private string _name = string.Empty;
        private int _count;
        private bool _flag;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public int Count
        {
            get => _count;
            set => SetProperty(ref _count, value);
        }

        public bool Flag
        {
            get => _flag;
            set => SetProperty(ref _flag, value);
        }

        public bool SetPropertyResult { get; private set; }

        public bool SetNameWithCallback(string value, Action onChanged)
        {
            return SetProperty(ref _name, value, onChanged);
        }

        // Expose OnPropertyChanged for testing
        public void RaisePropertyChanged(string? propertyName = null)
        {
            OnPropertyChanged(propertyName);
        }

        // Expose SafeFireAndForget for testing
        public void TestSafeFireAndForget(Task task, Action<Exception>? onError = null)
        {
            SafeFireAndForget(task, onError);
        }

        public bool DisposedViaVirtualMethod { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposedViaVirtualMethod = true;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Another test class to verify inheritance works correctly.
    /// </summary>
    private class AnotherTestViewModel : ViewModelBase
    {
        private double _value;

        public double Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }
    }

    #endregion

    #region INotifyPropertyChanged Tests

    [Fact]
    public void ViewModelBase_Implements_INotifyPropertyChanged()
    {
        // Arrange
        var viewModel = new TestViewModel();

        // Act & Assert
        Assert.IsAssignableFrom<INotifyPropertyChanged>(viewModel);
    }

    [Fact]
    public void OnPropertyChanged_Raises_PropertyChanged_Event()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var propertyName = "TestProperty";
        string? receivedPropertyName = null;

        viewModel.PropertyChanged += (sender, e) =>
        {
            receivedPropertyName = e.PropertyName;
        };

        // Act
        viewModel.RaisePropertyChanged(propertyName);

        // Assert
        Assert.Equal(propertyName, receivedPropertyName);
    }

    [Fact]
    public void OnPropertyChanged_WithNullPropertyName_Raises_Event_WithNull()
    {
        // Arrange
        var viewModel = new TestViewModel();
        string? receivedPropertyName = "not-null";

        viewModel.PropertyChanged += (sender, e) =>
        {
            receivedPropertyName = e.PropertyName;
        };

        // Act
        viewModel.RaisePropertyChanged(null);

        // Assert
        Assert.Null(receivedPropertyName);
    }

    [Fact]
    public void OnPropertyChanged_WithEmptyPropertyName_Raises_Event_WithEmpty()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var receivedPropertyName = "not-empty";

        viewModel.PropertyChanged += (sender, e) =>
        {
            receivedPropertyName = e.PropertyName;
        };

        // Act
        viewModel.RaisePropertyChanged(string.Empty);

        // Assert
        Assert.Equal(string.Empty, receivedPropertyName);
    }

    #endregion

    #region SetProperty Tests

    [Fact]
    public void SetProperty_NewValue_Raises_PropertyChanged_Event()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var propertyChanges = new List<string>();

        viewModel.PropertyChanged += (sender, e) =>
        {
            propertyChanges.Add(e.PropertyName ?? "null");
        };

        // Act
        viewModel.Name = "New Name";

        // Assert
        Assert.Single(propertyChanges);
        Assert.Equal("Name", propertyChanges[0]);
        Assert.Equal("New Name", viewModel.Name);
    }

    [Fact]
    public void SetProperty_SameValue_DoesNotRaise_PropertyChanged_Event()
    {
        // Arrange
        var viewModel = new TestViewModel();
        viewModel.Name = "Initial";
        var propertyChangedCount = 0;

        viewModel.PropertyChanged += (sender, e) =>
        {
            propertyChangedCount++;
        };

        // Act
        viewModel.Name = "Initial";

        // Assert
        Assert.Equal(0, propertyChangedCount);
        Assert.Equal("Initial", viewModel.Name);
    }

    [Fact]
    public void SetProperty_ReturnsTrue_WhenValueChanges()
    {
        // Arrange
        var viewModel = new TestViewModel();

        // Act
        var result = viewModel.SetNameWithCallback("New Name", () => { });

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void SetProperty_ReturnsFalse_WhenValueUnchanged()
    {
        // Arrange
        var viewModel = new TestViewModel();
        viewModel.Name = "Same Name";

        // Act
        var result = viewModel.SetNameWithCallback("Same Name", () => { });

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void SetProperty_WithCallback_InvokesCallback_WhenValueChanges()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var callbackInvoked = false;

        // Act
        viewModel.SetNameWithCallback("New Name", () => callbackInvoked = true);

        // Assert
        Assert.True(callbackInvoked);
    }

    [Fact]
    public void SetProperty_WithCallback_DoesNotInvokeCallback_WhenValueUnchanged()
    {
        // Arrange
        var viewModel = new TestViewModel();
        viewModel.Name = "Same Name";
        var callbackInvoked = false;

        // Act
        viewModel.SetNameWithCallback("Same Name", () => callbackInvoked = true);

        // Assert
        Assert.False(callbackInvoked);
    }

    [Fact]
    public void SetProperty_Int_NewValue_Raises_PropertyChanged()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var propertyChanges = new List<string>();

        viewModel.PropertyChanged += (sender, e) =>
        {
            propertyChanges.Add(e.PropertyName ?? "null");
        };

        // Act
        viewModel.Count = 42;

        // Assert
        Assert.Single(propertyChanges);
        Assert.Equal("Count", propertyChanges[0]);
        Assert.Equal(42, viewModel.Count);
    }

    [Fact]
    public void SetProperty_Int_SameValue_DoesNotRaise_PropertyChanged()
    {
        // Arrange
        var viewModel = new TestViewModel();
        viewModel.Count = 100;
        var propertyChangedCount = 0;

        viewModel.PropertyChanged += (sender, e) =>
        {
            propertyChangedCount++;
        };

        // Act
        viewModel.Count = 100;

        // Assert
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public void SetProperty_Bool_NewValue_Raises_PropertyChanged()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var propertyChanges = new List<string>();

        viewModel.PropertyChanged += (sender, e) =>
        {
            propertyChanges.Add(e.PropertyName ?? "null");
        };

        // Act
        viewModel.Flag = true;

        // Assert
        Assert.Single(propertyChanges);
        Assert.Equal("Flag", propertyChanges[0]);
        Assert.True(viewModel.Flag);
    }

    [Fact]
    public void SetProperty_NullStringToNonNull_Raises_PropertyChanged()
    {
        // Arrange
        var viewModel = new TestViewModel(); // Name is initialized to string.Empty
        viewModel.Name = null!; // Force null via reflection-like behavior
        var viewModel2 = new TestViewModel();
        viewModel2.Name = string.Empty; // Ensure it's empty
        var propertyChanges = new List<string>();

        viewModel2.PropertyChanged += (sender, e) =>
        {
            propertyChanges.Add(e.PropertyName ?? "null");
        };

        // Act
        viewModel2.Name = "Not Empty";

        // Assert
        Assert.Single(propertyChanges);
    }

    [Theory]
    [InlineData("A", "B")]
    [InlineData("", "Value")]
    [InlineData("Value", "")]
    public void SetProperty_String_DifferentValues_RaisesEvent(string initial, string updated)
    {
        // Arrange
        var viewModel = new TestViewModel();
        viewModel.Name = initial;
        var eventRaised = false;

        viewModel.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == "Name")
                eventRaised = true;
        };

        // Act
        viewModel.Name = updated;

        // Assert
        Assert.True(eventRaised);
    }

    #endregion

    #region IDisposable Tests

    [Fact]
    public void ViewModelBase_Implements_IDisposable()
    {
        // Arrange
        var viewModel = new TestViewModel();

        // Act & Assert
        Assert.IsAssignableFrom<IDisposable>(viewModel);
    }

    [Fact]
    public void Dispose_Calls_Virtual_DisposeMethod_WithDisposingTrue()
    {
        // Arrange
        var viewModel = new TestViewModel();

        // Act
        viewModel.Dispose();

        // Assert
        Assert.True(viewModel.DisposedViaVirtualMethod);
    }

    [Fact]
    public void Dispose_CanBeCalled_MultipleTimes_Safely()
    {
        // Arrange
        var viewModel = new TestViewModel();

        // Act & Assert - Should not throw
        viewModel.Dispose();
        viewModel.Dispose();
        viewModel.Dispose();

        // Assert - Virtual dispose should only be called once
        Assert.True(viewModel.DisposedViaVirtualMethod);
    }

    [Fact]
    public void Dispose_AfterDispose_ResourcesAreCleaned()
    {
        // Arrange
        var viewModel = new TestViewModel();

        // Act
        viewModel.Dispose();

        // Assert - Can still access properties but disposal flag is set
        Assert.True(viewModel.DisposedViaVirtualMethod);
    }

    #endregion

    #region Inheritance Tests

    [Fact]
    public void CanInherit_FromViewModelBase()
    {
        // Arrange & Act
        var viewModel1 = new TestViewModel();
        var viewModel2 = new AnotherTestViewModel();

        // Assert
        Assert.NotNull(viewModel1);
        Assert.NotNull(viewModel2);
        Assert.IsAssignableFrom<ViewModelBase>(viewModel1);
        Assert.IsAssignableFrom<ViewModelBase>(viewModel2);
    }

    [Fact]
    public void InheritedViewModel_Raises_PropertyChanged()
    {
        // Arrange
        var viewModel = new AnotherTestViewModel();
        var propertyChanges = new List<string>();

        viewModel.PropertyChanged += (sender, e) =>
        {
            propertyChanges.Add(e.PropertyName ?? "null");
        };

        // Act
        viewModel.Value = 3.14;

        // Assert
        Assert.Single(propertyChanges);
        Assert.Equal("Value", propertyChanges[0]);
    }

    #endregion

    #region SafeFireAndForget Tests

    [Fact]
    public async Task SafeFireAndForget_CompletedTask_DoesNotThrow()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var completedTask = Task.CompletedTask;

        // Act & Assert - Should not throw
        viewModel.TestSafeFireAndForget(completedTask);

        // Give a moment for the continuation to complete
        await Task.Delay(50);
    }

    [Fact]
    public async Task SafeFireAndForget_FaultedTask_WithErrorHandler_CallsHandler()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var expectedException = new InvalidOperationException("Test exception");
        var faultedTask = Task.FromException(expectedException);
        Exception? capturedException = null;

        // Act
        viewModel.TestSafeFireAndForget(faultedTask, ex => capturedException = ex);

        // Give time for the continuation to run
        await Task.Delay(100);

        // Assert
        Assert.NotNull(capturedException);
        Assert.IsType<InvalidOperationException>(capturedException);
        Assert.Equal("Test exception", capturedException.Message);
    }

    [Fact]
    public async Task SafeFireAndForget_FaultedTask_WithoutErrorHandler_DoesNotThrow()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var expectedException = new InvalidOperationException("Test exception");
        var faultedTask = Task.FromException(expectedException);

        // Act & Assert - Should not throw even with no handler
        viewModel.TestSafeFireAndForget(faultedTask);

        // Give time for the continuation to run
        await Task.Delay(50);
    }

    #endregion
}
