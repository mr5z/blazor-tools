using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BlazorAppTest2.Client;

public enum ErrorCode
{
	None = 0,
	NotFound = 1,
	Unauthorized = 2,
	ValidationFailed = 3,
	Timeout = 4,
}

public interface IResult
{
	bool IsSuccess { get; }
	bool IsFailure { get; }
	ErrorCode ErrorCode { get; }
	string? ErrorMessage { get; }
}

public readonly struct Result<T> : IResult
{
	public T? Value { get; }
	public bool IsSuccess { get; }
	public bool IsFailure => IsSuccess == false;
	public ErrorCode ErrorCode { get; }
	public string? ErrorMessage { get; }

	internal Result(T value)
	{
		Value = value;
		IsSuccess = true;
		ErrorCode = ErrorCode.None;
		ErrorMessage = null;
	}

	internal Result(ErrorCode errorCode, string errorMessage)
	{
		Value = default;
		IsSuccess = false;
		ErrorCode = errorCode;
		ErrorMessage = errorMessage;
	}

	public bool TryGetValue([NotNullWhen(true)] out T? value)
	{
		if (IsSuccess && Value is T outValue)
		{
			value = outValue;
			return true;
		}
		value = default;
		return false;
	}

	public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> func)
		=> TryGetValue(out var value)
		? func(value)
		: Result.Fail<TOut>(ErrorCode.None, ErrorMessage ?? DefaultErrorMessage);

	public Result<TOut> Map<TOut>(Func<T, TOut> func)
		=> TryGetValue(out var value)
		? Result.Ok(func(value))
		: Result.Fail<TOut>(ErrorCode.None, ErrorMessage ?? DefaultErrorMessage);

	private static readonly string DefaultErrorMessage = "An error occurred.";
}

public static class Result
{
	private readonly struct Unit
	{
		public static readonly Unit Instance = new();
	}

	public static IResult Ok()
		=> new Result<Unit>(value: Unit.Instance);

	public static IResult Fail(ErrorCode errorCode, string errorMessage)
		=> new Result<bool>(errorCode, errorMessage);

	public static Result<TValue> Ok<TValue>(TValue value)
		=> new(value);

	public static Result<TValue> Fail<TValue>(ErrorCode errorCode, string errorMessage)
		=> new(errorCode, errorMessage);
}

[DebuggerDisplay("{Name}")]
public readonly struct PageInfo(Type type)
{
	public Type Type { get; } = type;
	public string Name => Type.Name;
}

public interface INavigator
{
	IReadOnlyCollection<PageInfo> History { get; }

	IResult GoBack(IComponent component);

	bool CanGoBack { get; }
}

internal interface INavigationContext : INavigator
{
	IResult NavigateTo(Type component, object? parameters = null);
}

public interface IHostComponent
{
	IResult SetComponent(Type component, object? parameters = null);
}

public interface INavigationService : INavigator
{
	IResult NavigateTo<TPage>(IComponent fromPage, object? parameters = null) where TPage : IComponent;

	void RegisterRoot(IComponent root);

	void RegisterChild(IComponent child, IComponent root);
}

internal class RouteNavigationContext(NavigationManager navigationManager) : INavigationContext
{
	private readonly List<PageInfo> _history = [];

	public IReadOnlyCollection<PageInfo> History => _history.AsReadOnly();

	public bool CanGoBack => _history.Count > 1;

	IResult INavigationContext.NavigateTo(Type component, object? parameters)
	{
		var routeResult = GetRouteFromComponent(component);
		if (routeResult.TryGetValue(out var route))
		{
			var parsedRoute = ParseRoute(route, parameters);
			_history.Add(new PageInfo(component));
			navigationManager.NavigateTo(parsedRoute);
			return Result.Ok();
		}
		return routeResult;
	}

	IResult INavigator.GoBack(IComponent component)
	{
		if (CanGoBack == false)
		{
			return Result.Fail(ErrorCode.ValidationFailed, "Cannot go back");
		}

		_history.RemoveAt(_history.Count - 1);
		var previousPage = _history.Last();

		var routeResult = GetRouteFromComponent(previousPage.Type);
		if (routeResult.TryGetValue(out var route))
		{
			navigationManager.NavigateTo(route);
			return Result.Ok();
		}
		return routeResult;
	}

	private static Result<string> GetRouteFromComponent(Type component)
	{
		var attribute = component.GetCustomAttribute<RouteAttribute>();
		if (attribute is null)
		{
			return Result.Fail<string>(ErrorCode.ValidationFailed, "No route attribute found");
		}

		var route = attribute.Template;
		if (string.IsNullOrEmpty(route))
		{
			return Result.Fail<string>(ErrorCode.ValidationFailed, "Invalid route template");
		}

		return Result.Ok(route);
	}

	private static string ParseRoute(string route, object? parameters)
	{
		if (parameters is null) return route;

		var properties = parameters.GetType().GetProperties();
		var unusedParams = new List<string>();

		foreach (var prop in properties)
		{
			var value = prop.GetValue(parameters);
			if (value == null) continue;

			var valueStr = value.ToString()!;

			var simplePattern = $"{{{prop.Name}}}";
			var typedPatternRegex = new Regex($@"\{{{prop.Name}:[^}}]+\}}", RegexOptions.IgnoreCase);

			if (route.Contains(simplePattern, StringComparison.OrdinalIgnoreCase))
			{
				route = route.Replace(simplePattern, valueStr, StringComparison.OrdinalIgnoreCase);
			}
			else if (typedPatternRegex.IsMatch(route))
			{
				route = typedPatternRegex.Replace(route, valueStr);
			}
			else
			{
				unusedParams.Add($"{prop.Name}={Uri.EscapeDataString(valueStr)}");
			}
		}

		if (unusedParams.Count > 0)
		{
			var separator = route.Contains('?') ? '&' : '?';
			route += separator + string.Join('&', unusedParams);
		}

		return route;
	}
}

internal class SubPageNavigationContext(IHostComponent host) : INavigationContext
{
	private readonly IHostComponent _host = host;
	private readonly List<PageInfo> _history = [];

	public IReadOnlyCollection<PageInfo> History => _history.AsReadOnly();

	public bool CanGoBack => _history.Count > 1;

	private IResult NavigateInternal(Type component, object? parameters, bool ignoreHistory)
	{
		var result = _host.SetComponent(component, parameters);
		if (result.IsSuccess && ignoreHistory == false)
		{
			_history.Add(new PageInfo(component));
		}
		return result;
	}

	IResult INavigationContext.NavigateTo(Type component, object? parameters)
	{
		return NavigateInternal(component, parameters, ignoreHistory: false);
	}

	IResult INavigator.GoBack(IComponent component)
	{
		if (CanGoBack == false)
		{
			return Result.Fail(ErrorCode.ValidationFailed, "Cannot go back in sub-page navigation.");
		}

		_history.RemoveAt(_history.Count - 1);
		var previousPage = _history.Last();

		return NavigateInternal(previousPage.Type, parameters: null, ignoreHistory: true);
	}
}

public class NavigationService(NavigationManager navigationManager) : INavigationService
{
	private readonly NavigationManager _navigationManager = navigationManager;
	private INavigationContext? _currentContext;

	private readonly Dictionary<IComponent, INavigationContext> _contexts = [];
	private readonly Dictionary<IComponent, IComponent> _ownershipMap = [];

	public IReadOnlyCollection<PageInfo> History => _currentContext?.History ?? [];

	public bool CanGoBack => _currentContext?.CanGoBack == true;

	public IResult NavigateTo<TPage>(IComponent component, object? parameters) where TPage : IComponent
	{
		if (TryGetContext(component, out var context) == false)
		{
			return Result.Fail(ErrorCode.ValidationFailed, "No root NavigationPage found");
		}

		_currentContext = context;

		return context.NavigateTo(typeof(TPage), parameters);
	}

	public IResult GoBack(IComponent component)
	{
		if (TryGetContext(component, out var context) == false)
		{
			return Result.Fail(ErrorCode.ValidationFailed, "No root NavigationPage found");
		}

		_currentContext = context;

		return context.GoBack(component);
	}

	public void RegisterRoot(IComponent root)
	{
		if (_contexts.ContainsKey(root) == false)
		{
			if (root is IHostComponent host)
			{
				_contexts[root] = new SubPageNavigationContext(host);
			}
			else
			{
				_contexts[root] = new RouteNavigationContext(_navigationManager);
			}
		}
	}

	public void RegisterChild(IComponent child, IComponent root)
	{
		RegisterRoot(root);

		_ownershipMap[child] = root;
	}

	private IComponent? ResolveRoot(IComponent component)
	{
		if (_contexts.ContainsKey(component))
		{
			return component;
		}

		if (_ownershipMap.TryGetValue(component, out var owner))
		{
			return owner;
		}

		return null;
	}

	private bool TryGetContext(IComponent component, [NotNullWhen(true)] out INavigationContext? context)
	{
		var root = ResolveRoot(component);
		if (root is null)
		{
			context = null;
			return false;
		}
		context = _contexts[root];
		return true;
	}
}

public class SubPageHostComponent : ComponentBase, IHostComponent
{
	protected Type? _activeComponent;
	protected Dictionary<string, object?>? _activeParameters;

	protected RenderFragment SubPage => builder =>
	{
		if (_activeComponent is null)
		{
			return;
		}

		builder.OpenComponent(0, typeof(DynamicComponent));
		builder.AddAttribute(1, nameof(DynamicComponent.Type), _activeComponent);
		builder.AddAttribute(2, nameof(DynamicComponent.Parameters), _activeParameters);
		builder.CloseComponent();
	};

	IResult IHostComponent.SetComponent(Type component, object? parameters)
	{
		return UpdateSubPage(component, parameters);
	}

	private IResult UpdateSubPage(Type component, object? parameters = null)
	{
		if (typeof(IComponent).IsAssignableFrom(component) == false)
		{
			return Result.Fail(ErrorCode.ValidationFailed, 
				$"'{component.Name}' must implement '{nameof(IComponent)}'"
			);
		}

		_activeComponent = component;
		_activeParameters = MapToDictionary(component, parameters);
		StateHasChanged();
		return Result.Ok();
	}

	private static Dictionary<string, object?> MapToDictionary(Type type, object? kvp)
	{
		var dictionary = new Dictionary<string, object?>();

		if (kvp is null)
		{
			return dictionary;
		}

		var inputProps = kvp.GetType()
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.ToDictionary(p => p.Name, p => p.GetValue(kvp));

		var parameterProps = type
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.IsDefined(typeof(ParameterAttribute), inherit: true));

		foreach (var prop in parameterProps)
		{
			if (inputProps.TryGetValue(prop.Name, out var value))
			{
				dictionary[prop.Name] = value;
			}
		}

		return dictionary;
	}
}

public class NavigationPage : SubPageHostComponent
{
	protected override void BuildRenderTree(RenderTreeBuilder builder)
	{
		if (_activeComponent is not null)
		{
			builder.OpenComponent(0, typeof(CascadingValue<NavigationPage>));
			builder.AddAttribute(1, "Value", this);
			builder.AddAttribute(2, "IsFixed", true);
			builder.AddAttribute(3, "ChildContent", (RenderFragment)(childBuilder =>
			{
				childBuilder.OpenComponent(0, typeof(DynamicComponent));
				childBuilder.AddAttribute(1, nameof(DynamicComponent.Type), _activeComponent);
				childBuilder.AddAttribute(2, nameof(DynamicComponent.Parameters), _activeParameters);
				childBuilder.CloseComponent();
			}));
			builder.CloseComponent();
		}
	}
}