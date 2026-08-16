using System.Collections.Immutable;

namespace Correntra.Core.Categories;

public sealed class CategoryRouter
{
    private readonly ImmutableArray<DownloadCategory> _categories;
    private readonly ImmutableArray<CategoryRule> _rules;

    public CategoryRouter(IEnumerable<DownloadCategory> categories, IEnumerable<CategoryRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(categories);
        _categories = categories.ToImmutableArray();
        if (_categories.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one download category is required.", nameof(categories));
        }

        if (_categories.GroupBy(static category => category.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Download category IDs must be unique.", nameof(categories));
        }

        _rules = (rules ?? []).OrderByDescending(static rule => rule.Priority).ThenBy(static rule => rule.Id.Value).ToImmutableArray();
        if (_rules.GroupBy(static rule => rule.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Category rule IDs must be unique.", nameof(rules));
        }

        ImmutableHashSet<CategoryId> categoryIds = _categories.Select(static category => category.Id).ToImmutableHashSet();
        if (_rules.Any(rule => !categoryIds.Contains(rule.CategoryId)))
        {
            throw new ArgumentException("Every category rule must target an existing category.", nameof(rules));
        }
    }

    public ImmutableArray<DownloadCategory> Categories => _categories;

    public ImmutableArray<CategoryRule> Rules => _rules;

    public DownloadCategory? Route(CategoryMatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CategoryRule? matchingRule = _rules.FirstOrDefault(rule => rule.Matches(context));
        if (matchingRule is not null)
        {
            return _categories.First(category => category.Id == matchingRule.CategoryId);
        }

        return _categories.FirstOrDefault(category => category.Matches(context));
    }
}
