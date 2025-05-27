using Microsoft.AspNetCore.Components;

namespace BlazorAppTest2.Client.Pages;

public class SubPage : ComponentBase
{
	[CascadingParameter]
	protected NavigationPage Owner { get; set; } = default!;

	[Inject]
	protected INavigationService NavigationService { get; set; } = default!;

	protected override void OnInitialized()
	{
		base.OnInitialized();
		NavigationService.RegisterChild(this, Owner);
	}

	protected override async Task OnInitializedAsync()
	{
		await base.OnInitializedAsync();
		NavigationService.RegisterChild(this, Owner);
	}
}
