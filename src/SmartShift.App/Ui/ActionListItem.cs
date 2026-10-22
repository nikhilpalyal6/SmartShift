using System.Windows.Media;
using SmartShift.Core.Actions;

namespace SmartShift.App.Ui;

public sealed class ActionListItem
{
    public ActionListItem(ActionRecommendation recommendation)
    {
        Recommendation = recommendation;
        Title = recommendation.Title;
        Description = recommendation.Description;
        GoalTitle = recommendation.GoalTitle;
        Badge = recommendation.IsAvailable ? "Available" : "Not implemented";
        var app = System.Windows.Application.Current;
        BadgeBrush = (Brush)app.FindResource(recommendation.IsAvailable ? "OkSoftBrush" : "WarnSoftBrush");
        BadgeForeground = (Brush)app.FindResource(recommendation.IsAvailable ? "OkBrush" : "WarnBrush");
    }

    public ActionRecommendation Recommendation { get; }
    public string Title { get; }
    public string Description { get; }
    public string GoalTitle { get; }
    public string Badge { get; }
    public Brush BadgeBrush { get; }
    public Brush BadgeForeground { get; }
}
