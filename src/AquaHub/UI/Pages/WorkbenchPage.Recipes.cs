using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AquaHub.Core.Agents;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.UI.Pages;

public partial class WorkbenchPage
{
    private AgentRecipe? _editingRecipe;
    private Button? _recipeEntry;
    private bool _recipeBusy;

    private void OnRecipesChanged() => Hub.OnUi(() =>
    {
        if (IsVisible && RecipesTab.IsChecked == true) RefreshRecipes();
    });

    private void RefreshRecipes()
    {
        var service = Hub.Core.Recipes;
        var recipes = service.State.Recipes;
        if (SavedRecipes.ItemsSource is not ImmutableArray<AgentRecipe> shown || shown != recipes) SavedRecipes.ItemsSource = recipes;
        NewRecipe.IsEnabled = service.StoreAvailable && !_recipeBusy;
        NoRecipes.Visibility = recipes.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoRecipes.Text = service.StoreAvailable ? "Create a recipe to check your PC and review the findings." : "Saved recipes could not be opened.";
    }

    private void OnNewRecipe(object sender, RoutedEventArgs e) => OpenRecipeEditor(null, (Button)sender);

    private void OnEditRecipe(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgentRecipe recipe } entry) OpenRecipeEditor(recipe, entry);
    }

    private void OnViewRecipe(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgentRecipe recipe }) Hub.Windows.ShowMain("agents", $"recipe:{recipe.Id}");
    }

    private void OpenRecipeEditor(AgentRecipe? recipe, Button? entry = null)
    {
        if (_recipeBusy) return;
        _editingRecipe = recipe;
        _recipeEntry = entry;
        RecipeName.Text = recipe?.Draft.Name ?? "PC health briefing";
        RecipePurpose.Text = recipe?.Draft.Purpose ?? "Review Windows health findings";
        RecipeReport.IsChecked = recipe is null || recipe.Draft.Steps.Length == 2;
        RecipeScope.SelectedIndex = recipe?.Draft.Steps.LastOrDefault()?.Findings == RecipeFindingScope.All ? 1 : 0;
        DeleteRecipe.Visibility = recipe is null ? Visibility.Collapsed : Visibility.Visible;
        SavedRecipesCard.Visibility = Visibility.Collapsed;
        RecipeEditor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            if (!RecipeEditor.IsVisible) return;
            RecipeName.BringIntoView();
            if (Window.GetWindow(this)?.IsActive == true)
            {
                RecipeName.Focus();
                RecipeName.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    private async void OnSaveRecipe(object sender, RoutedEventArgs e)
    {
        var draft = ImmutableArray.CreateBuilder<RecipeStep>(2);
        draft.Add(new(RecipeCapabilities.CheckHealth));
        if (RecipeReport.IsChecked == true)
            draft.Add(new(RecipeCapabilities.BuildReport, 1, RecipeScope.SelectedIndex == 1 ? RecipeFindingScope.All : RecipeFindingScope.Actionable));
        var input = new RecipeDraft(RecipeName.Text, RecipePurpose.Text, draft.ToImmutable());
        await ChangeRecipeAsync(async () =>
        {
            var saved = await Hub.Core.Recipes.SaveAsync(_editingRecipe?.Id, _editingRecipe?.Revision, input);
            CloseRecipeEditor();
            if (IsVisible) WorkbenchFeedback.Show($"Saved {saved.Draft.Name}.", actionLabel: "View in Agents",
                action: () => Hub.Windows.ShowMain("agents", $"recipe:{saved.Id}"));
        });
    }

    private async void OnDeleteRecipe(object sender, RoutedEventArgs e)
    {
        if (_editingRecipe is not { } recipe || _recipeBusy) return;
        if (MessageBox.Show(Window.GetWindow(this)!, "Delete this recipe and its run history? An active run will be cancelled.",
                "Delete recipe", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await ChangeRecipeAsync(async () =>
        {
            await Hub.Core.Recipes.DeleteAsync(recipe.Id, recipe.Revision);
            CloseRecipeEditor();
            if (IsVisible) WorkbenchFeedback.Show("Recipe deleted.");
        });
    }

    private void OnCloseRecipeEditor(object sender, RoutedEventArgs e)
    {
        if (!_recipeBusy) CloseRecipeEditor();
    }

    private void OnRecipeEditorKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _recipeBusy) return;
        CloseRecipeEditor();
        e.Handled = true;
    }

    private void CloseRecipeEditor()
    {
        RecipeEditor.Visibility = Visibility.Collapsed;
        SavedRecipesCard.Visibility = Visibility.Visible;
        _editingRecipe = null;
        if (IsVisible && RecipesTab.IsChecked == true)
        {
            RefreshRecipes();
            Dispatcher.BeginInvoke(() =>
            {
                if (!RecipesPanel.IsVisible || RecipeEditor.IsVisible || Window.GetWindow(this)?.IsActive != true) return;
                (_recipeEntry is { IsVisible: true, IsLoaded: true } ? _recipeEntry : NewRecipe).Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    private async Task ChangeRecipeAsync(Func<Task> action)
    {
        if (_recipeBusy) return;
        _recipeBusy = true;
        RecipeEditor.IsEnabled = false;
        try { await action(); }
        catch (RecipeException ex) { if (IsVisible) WorkbenchFeedback.Show(ex.Message, error: true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("recipes", "Could not save recipe changes", ex);
            if (IsVisible) WorkbenchFeedback.Show("Couldn’t save the change. Try again.", error: true);
        }
        finally
        {
            _recipeBusy = false;
            RecipeEditor.IsEnabled = true;
            if (IsVisible) RefreshRecipes();
        }
    }
}
