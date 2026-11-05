using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SmartShift.App.Ui;
using SmartShift.Core.Classification;
using SmartShift.Processing;
using SmartShift.Processing.PresentationStudio.Import;
using SmartShift.Processing.PresentationStudio.Models;
using SmartShift.Processing.PresentationStudio.Outline;

namespace SmartShift.App;

public partial class MainWindow : Window
{
    // ============================================================
    // STEP 4: TEXT-TO-PRESENTATION STUDIO LOGIC & STATE
    // ============================================================
    private PresentationDeck? _presDeck;
    private PresentationOutline? _presOutline;
    private ExtractedDocumentContent? _presExtractedContent;
    private readonly ObservableCollection<SlideItemViewModel> _presSlides = [];
    private readonly ObservableCollection<OutlineItemViewModel> _presOutlineDrafts = [];
    private int _presSelectedSlideIndex = -1;
    private bool _presHasUnsavedChanges = false;
    private bool _isExportingPresentation = false;
    private CancellationTokenSource? _presExportCts;
    private bool _presUpdatingUiFromModel = false;
    private string? _lastGeneratedPresentationPath;

    private void InitializePresentationStudio()
    {
        PresOutlineDraftList.ItemsSource = _presOutlineDrafts;
        PresSlideThumbnailsList.ItemsSource = _presSlides;
    }

    public void OpenPresentationStudio(ClassifiedFile? targetFile = null)
    {
        if (ImageEditorHost.Visibility == Visibility.Visible) CloseImageEditor();
        if (BatchStudioHost.Visibility == Visibility.Visible) CloseBatchStudio();
        if (DocumentBuilderHost.Visibility == Visibility.Visible) CloseDocumentBuilder();
        if (PdfMergeStudioHost.Visibility == Visibility.Visible) ClosePdfMergeStudio();
        if (PdfSplitStudioHost.Visibility == Visibility.Visible) ClosePdfSplitStudio();
        if (PdfOrganizeStudioHost.Visibility == Visibility.Visible) ClosePdfOrganizeStudio();
        ClosePdfPreview();

        PresentationStudioHost.Visibility = Visibility.Visible;
        NavPresentationBtn.Tag = "Active";
        NavWorkspaceBtn.Tag = null;
        NavQueueBtn.Tag = null;
        NavDocBuilderBtn.Tag = null;
        NavPdfMergeBtn.Tag = null;
        NavPdfSplitBtn.Tag = null;
        NavPdfOrganizeBtn.Tag = null;
        NavPdfMergeBtn.Tag = null;
        NavPdfSplitBtn.Tag = null;

        if (targetFile is not null && App.Services.PresentationStudio.Importers.IsExtensionSupported(targetFile.Extension))
        {
            _ = LoadDocumentIntoPresentationStudioAsync(targetFile.Path);
        }
        else if (_selectedFileItem?.File is not null && _presDeck is null && _presOutline is null &&
                 App.Services.PresentationStudio.Importers.IsExtensionSupported(_selectedFileItem.File.Extension))
        {
            _ = LoadDocumentIntoPresentationStudioAsync(_selectedFileItem.File.Path);
        }
        else if (_presDeck is null && _presOutline is null)
        {
            InitializeDefaultDeck();
        }
    }

    public void ClosePresentationStudio()
    {
        if (_presHasUnsavedChanges)
        {
            var res = MessageBox.Show(
                "You have unsaved changes in your presentation. Are you sure you want to close and discard changes?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;
        }

        PresentationStudioHost.Visibility = Visibility.Collapsed;
        NavPresentationBtn.Tag = null;
        NavWorkspaceBtn.Tag = "Active";
    }

    private void NavPresentation_Click(object sender, RoutedEventArgs e)
    {
        RecentResultsFlyout.Visibility = Visibility.Collapsed;
        SettingsFlyout.Visibility = Visibility.Collapsed;
        OpenPresentationStudio();
    }

    private void ClosePresentationStudio_Click(object sender, RoutedEventArgs e) => ClosePresentationStudio();

    // ============================================================
    // DOCUMENT IMPORT & OUTLINE REVIEW LOGIC
    // ============================================================

    private async Task LoadDocumentIntoPresentationStudioAsync(string filePath)
    {
        try
        {
            ShowBanner($"Importing {Path.GetFileName(filePath)} for presentation…", error: false);
            var content = await App.Services.PresentationStudio.ImportDocumentAsync(filePath, CancellationToken.None);
            _presExtractedContent = content;

            PresOutlineSourceFileText.Text = Path.GetFileName(content.SourceFilePath);
            PresOutlineCharCountText.Text = $"{content.TotalCharacters:N0} characters · {content.Sections.Count} sections";

            if (content.HasScannedContentOnly)
            {
                PresOutlineWarningBanner.Visibility = Visibility.Visible;
                PresOutlineWarningText.Text = content.WarningMessage ?? "This PDF appears to be a scanned document without extractable text. OCR is required.";
            }
            else
            {
                PresOutlineWarningBanner.Visibility = Visibility.Collapsed;
            }

            var options = GetCurrentOutlineOptions();
            var outline = App.Services.PresentationStudio.GenerateOutline(content, options);
            _presOutline = outline;
            PresDeckTitleBox.Text = outline.PresentationTitle;

            PopulateOutlineDrafts(outline);
            ShowOutlineView();
            ShowBanner("Document outline generated successfully.", error: false);
        }
        catch (Exception ex)
        {
            App.Logger.Error("pres", $"Failed to import presentation document: {filePath}", ex);
            ShowBanner($"Could not import presentation document: {ex.Message}", error: true);
            if (_presDeck is null) InitializeDefaultDeck();
        }
    }

    private OutlineGenerationOptions GetCurrentOutlineOptions()
    {
        var density = SlideContentDensity.Standard;
        if (PresDensityCompactRadio.IsChecked == true) density = SlideContentDensity.Compact;
        else if (PresDensitySpaciousRadio.IsChecked == true) density = SlideContentDensity.Spacious;

        return new OutlineGenerationOptions
        {
            GenerateTitleSlide = PresOutlineTitleSlideCheck.IsChecked == true,
            UseHeadingsAsSlideTitles = PresOutlineHeadingsAsTitlesCheck.IsChecked == true,
            ContentDensity = density
        };
    }

    private void PopulateOutlineDrafts(PresentationOutline outline)
    {
        _presOutlineDrafts.Clear();
        int seq = 1;
        foreach (var draft in outline.Slides)
        {
            _presOutlineDrafts.Add(new OutlineItemViewModel(draft, seq++));
        }
        UpdateOutlineMoveButtons();
    }

    private void UpdateOutlineMoveButtons()
    {
        for (int i = 0; i < _presOutlineDrafts.Count; i++)
        {
            _presOutlineDrafts[i].CanMoveUp = i > 0;
            _presOutlineDrafts[i].CanMoveDown = i < _presOutlineDrafts.Count - 1;
        }
    }

    private void PresImportDocument_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Choose Document to Import as Presentation",
            Filter = "Supported Documents (*.txt;*.md;*.docx;*.pdf)|*.txt;*.text;*.md;*.markdown;*.docx;*.pdf|Text Files (*.txt)|*.txt|Markdown (*.md)|*.md|Word Documents (*.docx)|*.docx|PDF Documents (*.pdf)|*.pdf|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (ofd.ShowDialog() == true)
        {
            _ = LoadDocumentIntoPresentationStudioAsync(ofd.FileName);
        }
    }

    private void PresNewBlank_Click(object sender, RoutedEventArgs e)
    {
        if (_presHasUnsavedChanges)
        {
            var res = MessageBox.Show(
                "Discard unsaved changes and start a new presentation?",
                "New Presentation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
        }

        InitializeDefaultDeck();
    }

    private void PresRegenerateOutline_Click(object sender, RoutedEventArgs e)
    {
        if (_presExtractedContent is null) return;
        var options = GetCurrentOutlineOptions();
        _presOutline = App.Services.PresentationStudio.GenerateOutline(_presExtractedContent, options);
        PopulateOutlineDrafts(_presOutline);
    }

    private void PresOutlineMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: OutlineItemViewModel item })
        {
            int idx = _presOutlineDrafts.IndexOf(item);
            if (idx > 0)
            {
                _presOutlineDrafts.Move(idx, idx - 1);
                RenumberOutline();
            }
        }
    }

    private void PresOutlineMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: OutlineItemViewModel item })
        {
            int idx = _presOutlineDrafts.IndexOf(item);
            if (idx >= 0 && idx < _presOutlineDrafts.Count - 1)
            {
                _presOutlineDrafts.Move(idx, idx + 1);
                RenumberOutline();
            }
        }
    }

    private void PresOutlineDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: OutlineItemViewModel item })
        {
            _presOutlineDrafts.Remove(item);
            RenumberOutline();
        }
    }

    private void PresOutlineAddDraft_Click(object sender, RoutedEventArgs e)
    {
        var draft = new OutlineSlideDraft
        {
            Title = $"Section {_presOutlineDrafts.Count + 1}",
            SuggestedLayout = PresentationLayoutType.TitleAndContent,
            BulletPoints = ["Key takeaway or topic point"]
        };
        _presOutlineDrafts.Add(new OutlineItemViewModel(draft, _presOutlineDrafts.Count + 1));
        UpdateOutlineMoveButtons();
    }

    private void RenumberOutline()
    {
        for (int i = 0; i < _presOutlineDrafts.Count; i++)
        {
            _presOutlineDrafts[i].SequenceNumber = i + 1;
        }
        UpdateOutlineMoveButtons();
    }

    private void ShowOutlineView()
    {
        PresOutlineView.Visibility = Visibility.Visible;
        PresEditorView.Visibility = Visibility.Collapsed;
        PresTabOutlineBtn.Style = (Style)FindResource("PrimaryButton");
        PresTabEditorBtn.Style = (Style)FindResource("SecondaryButton");
    }

    private void ShowEditorView()
    {
        PresOutlineView.Visibility = Visibility.Collapsed;
        PresEditorView.Visibility = Visibility.Visible;
        PresTabOutlineBtn.Style = (Style)FindResource("SecondaryButton");
        PresTabEditorBtn.Style = (Style)FindResource("PrimaryButton");
    }

    private void PresTabOutline_Click(object sender, RoutedEventArgs e) => ShowOutlineView();
    private void PresTabEditor_Click(object sender, RoutedEventArgs e) => ShowEditorView();

    private async void PresOutlineProceed_Click(object sender, RoutedEventArgs e)
    {
        if (_presOutlineDrafts.Count == 0)
        {
            ShowBanner("Add at least one section before proceeding to the editor.", error: false, warning: true);
            return;
        }

        var outline = new PresentationOutline
        {
            PresentationTitle = !string.IsNullOrWhiteSpace(PresDeckTitleBox.Text) ? PresDeckTitleBox.Text.Trim() : "Presentation",
            SourceFilePath = _presExtractedContent?.SourceFilePath ?? string.Empty,
            Slides = _presOutlineDrafts.Select(o => o.Draft).ToList()
        };

        _presDeck = PresentationDeck.FromOutline(outline, _presDeck?.Theme ?? PresentationTheme.DarkSlate);
        PopulateSlidesFromDeck(_presDeck);
        ShowEditorView();
        ShowBanner("Presentation outline accepted.", error: false);
    }

    // ============================================================
    // SLIDE DECK & INTERACTIVE EDITOR LOGIC
    // ============================================================

    private void InitializeDefaultDeck()
    {
        _presDeck = PresentationDeck.CreateDefault("New Presentation");
        PresDeckTitleBox.Text = _presDeck.Title;
        _presExtractedContent = null;
        _presOutline = null;
        _presHasUnsavedChanges = false;
        PresUnsavedBadge.Visibility = Visibility.Collapsed;
        PopulateSlidesFromDeck(_presDeck);
        ShowEditorView();
    }

    private void PopulateSlidesFromDeck(PresentationDeck deck)
    {
        _presSlides.Clear();
        int num = 1;
        foreach (var slide in deck.Slides)
        {
            _presSlides.Add(new SlideItemViewModel(slide, num++));
        }
        PresSlideCountHeader.Text = $"SLIDES ({_presSlides.Count})";
        UpdateSlideMoveButtons();

        if (_presSlides.Count > 0)
        {
            SelectSlide(0);
        }
    }

    private void SelectSlide(int index)
    {
        if (index < 0 || index >= _presSlides.Count) return;
        _presSelectedSlideIndex = index;
        PresSlideThumbnailsList.SelectedIndex = index;

        for (int i = 0; i < _presSlides.Count; i++)
        {
            _presSlides[i].IsSelected = (i == index);
        }

        var selectedVm = _presSlides[index];
        PresEditorSlideIndexText.Text = $"Slide {index + 1} of {_presSlides.Count}";
        PresEditorSlideLayoutText.Text = selectedVm.Slide.Layout.ToString();

        _presUpdatingUiFromModel = true;
        try
        {
            PresEditSlideTitleBox.Text = selectedVm.Slide.Title;

            if (selectedVm.Slide.Layout is PresentationLayoutType.Title or PresentationLayoutType.SectionDivider)
            {
                PresEditPrimaryContentLabel.Text = "Subtitle / Overview";
                PresEditPrimaryContentBox.Text = selectedVm.Slide.PrimaryParagraph ?? "";
                PresEditSecondaryBlock.Visibility = Visibility.Collapsed;
            }
            else if (selectedVm.Slide.Layout == PresentationLayoutType.TwoColumn)
            {
                PresEditPrimaryContentLabel.Text = "Left Column Items (One per line)";
                PresEditPrimaryContentBox.Text = string.Join(Environment.NewLine, selectedVm.Slide.PrimaryBullets);
                PresEditSecondaryBlock.Visibility = Visibility.Visible;
                PresEditSecondaryContentBox.Text = string.Join(Environment.NewLine, selectedVm.Slide.SecondaryBullets);
            }
            else
            {
                PresEditPrimaryContentLabel.Text = "Bullet Points (One per line)";
                PresEditPrimaryContentBox.Text = string.Join(Environment.NewLine, selectedVm.Slide.PrimaryBullets);
                PresEditSecondaryBlock.Visibility = Visibility.Collapsed;
            }

            PresRemoveImageBtn.Visibility = selectedVm.Slide.Image is not null ? Visibility.Visible : Visibility.Collapsed;

            // Provenance
            if (!string.IsNullOrWhiteSpace(selectedVm.Slide.SourceProvenance))
            {
                PresProvenanceCard.Visibility = Visibility.Visible;
                PresProvenanceCardText.Text = selectedVm.Slide.SourceProvenance;
            }
            else
            {
                PresProvenanceCard.Visibility = Visibility.Collapsed;
            }

            // Sync layout buttons
            UpdateLayoutButtons(selectedVm.Slide.Layout);
            UpdateAspectAndThemeButtons();
            RefreshLiveSlidePreview(selectedVm.Slide);
        }
        finally
        {
            _presUpdatingUiFromModel = false;
        }
    }

    private void PresSlideThumbnailsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_presUpdatingUiFromModel) return;
        if (PresSlideThumbnailsList.SelectedIndex >= 0)
        {
            SelectSlide(PresSlideThumbnailsList.SelectedIndex);
        }
    }

    private void RefreshLiveSlidePreview(PresentationSlide slide)
    {
        var theme = _presDeck?.Theme ?? PresentationTheme.DarkSlate;

        Brush bgBrush = Brushes.Black;
        Brush textBrush = Brushes.White;
        Brush accentBrush = Brushes.Orange;
        Brush mutedBrush = Brushes.Gray;

        try
        {
            bgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.BackgroundHex));
            textBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.PrimaryTextHex));
            accentBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.AccentHex));
            mutedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MutedTextHex));
        }
        catch { }

        var fontFamily = new FontFamily(theme.FontFamily);

        PresSlidePreviewBox.Background = bgBrush;
        PresSlidePreviewTitle.Foreground = textBrush;
        PresSlidePreviewTitle.FontFamily = fontFamily;
        PresSlidePreviewTitle.FontSize = theme.TitleFontSize * 0.55;
        PresSlidePreviewTitle.Text = string.IsNullOrWhiteSpace(slide.Title) ? "(Slide Title)" : slide.Title;

        PresSlidePreviewTitle.TextAlignment = theme.Alignment == SlideTextAlignment.Center
            ? TextAlignment.Center
            : TextAlignment.Left;

        // Check overflow
        if (slide.HasLikelyOverflow(out var overflowMsg))
        {
            PresOverflowBanner.Visibility = Visibility.Visible;
            PresOverflowBannerText.Text = overflowMsg;
        }
        else
        {
            PresOverflowBanner.Visibility = Visibility.Collapsed;
        }

        switch (slide.Layout)
        {
            case PresentationLayoutType.Title:
                PresSlidePreviewTitle.FontSize = (theme.TitleFontSize + 8) * 0.55;
                PresSlidePreviewTitle.Foreground = accentBrush;
                PresSlidePreviewSubtitle.Visibility = Visibility.Visible;
                PresSlidePreviewSubtitle.Foreground = mutedBrush;
                PresSlidePreviewSubtitle.FontFamily = fontFamily;
                PresSlidePreviewSubtitle.Text = slide.PrimaryParagraph ?? "Presentation Subtitle";
                PresSlidePreviewParagraph.Visibility = Visibility.Collapsed;
                PresSlidePreviewPrimaryBullets.ItemsSource = null;
                PresSlidePreviewCol2Container.Visibility = Visibility.Collapsed;
                break;

            case PresentationLayoutType.SectionDivider:
                PresSlidePreviewTitle.FontSize = (theme.TitleFontSize + 6) * 0.55;
                PresSlidePreviewTitle.Foreground = accentBrush;
                PresSlidePreviewSubtitle.Visibility = Visibility.Visible;
                PresSlidePreviewSubtitle.Foreground = mutedBrush;
                PresSlidePreviewSubtitle.FontFamily = fontFamily;
                PresSlidePreviewSubtitle.Text = slide.PrimaryParagraph ?? "";
                PresSlidePreviewParagraph.Visibility = Visibility.Collapsed;
                PresSlidePreviewPrimaryBullets.ItemsSource = null;
                PresSlidePreviewCol2Container.Visibility = Visibility.Collapsed;
                break;

            case PresentationLayoutType.TwoColumn:
                PresSlidePreviewSubtitle.Visibility = Visibility.Collapsed;
                PresSlidePreviewParagraph.Visibility = Visibility.Collapsed;
                PresSlidePreviewPrimaryBullets.ItemsSource = slide.PrimaryBullets;
                PresSlidePreviewCol2Container.Visibility = Visibility.Visible;
                PresSlidePreviewSecondaryBullets.Visibility = Visibility.Visible;
                PresSlidePreviewSecondaryBullets.ItemsSource = slide.SecondaryBullets;
                PresSlidePreviewImage.Visibility = Visibility.Collapsed;
                break;

            case PresentationLayoutType.TextWithImage:
                PresSlidePreviewSubtitle.Visibility = Visibility.Collapsed;
                PresSlidePreviewParagraph.Visibility = Visibility.Collapsed;
                PresSlidePreviewPrimaryBullets.ItemsSource = slide.PrimaryBullets;
                PresSlidePreviewCol2Container.Visibility = Visibility.Visible;
                PresSlidePreviewSecondaryBullets.Visibility = Visibility.Collapsed;
                if (slide.Image is not null && File.Exists(slide.Image.ImagePath))
                {
                    PresSlidePreviewImage.Visibility = Visibility.Visible;
                    PresSlidePreviewImage.Source = new BitmapImage(new Uri(slide.Image.ImagePath));
                }
                else
                {
                    PresSlidePreviewImage.Visibility = Visibility.Collapsed;
                }
                break;

            case PresentationLayoutType.ImageFocused:
                PresSlidePreviewSubtitle.Visibility = Visibility.Collapsed;
                PresSlidePreviewParagraph.Visibility = Visibility.Collapsed;
                PresSlidePreviewPrimaryBullets.ItemsSource = null;
                PresSlidePreviewCol2Container.Visibility = Visibility.Visible;
                PresSlidePreviewSecondaryBullets.Visibility = Visibility.Collapsed;
                if (slide.Image is not null && File.Exists(slide.Image.ImagePath))
                {
                    PresSlidePreviewImage.Visibility = Visibility.Visible;
                    PresSlidePreviewImage.Source = new BitmapImage(new Uri(slide.Image.ImagePath));
                }
                else
                {
                    PresSlidePreviewImage.Visibility = Visibility.Collapsed;
                }
                break;

            case PresentationLayoutType.Blank:
                PresSlidePreviewTitle.Text = "";
                PresSlidePreviewSubtitle.Visibility = Visibility.Collapsed;
                PresSlidePreviewParagraph.Visibility = Visibility.Collapsed;
                PresSlidePreviewPrimaryBullets.ItemsSource = null;
                PresSlidePreviewCol2Container.Visibility = Visibility.Collapsed;
                break;

            case PresentationLayoutType.TitleAndContent:
            default:
                PresSlidePreviewSubtitle.Visibility = Visibility.Collapsed;
                PresSlidePreviewParagraph.Visibility = Visibility.Collapsed;
                PresSlidePreviewPrimaryBullets.ItemsSource = slide.PrimaryBullets;
                PresSlidePreviewCol2Container.Visibility = Visibility.Collapsed;
                break;
        }

        PresSlidePreviewProvenanceText.Text = slide.SourceProvenance ?? "";
    }

    private void PresDeckTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_presUpdatingUiFromModel || _presDeck is null) return;
        _presDeck.Title = PresDeckTitleBox.Text.Trim();
        MarkPresentationDirty();
    }

    private void PresEditSlideTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_presUpdatingUiFromModel || _presSelectedSlideIndex < 0 || _presSelectedSlideIndex >= _presSlides.Count) return;
        var vm = _presSlides[_presSelectedSlideIndex];
        vm.Title = PresEditSlideTitleBox.Text;
        vm.NotifySlideChanged();
        RefreshLiveSlidePreview(vm.Slide);
        MarkPresentationDirty();
    }

    private void PresEditPrimaryContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_presUpdatingUiFromModel || _presSelectedSlideIndex < 0 || _presSelectedSlideIndex >= _presSlides.Count) return;
        var vm = _presSlides[_presSelectedSlideIndex];

        if (vm.Slide.Layout is PresentationLayoutType.Title or PresentationLayoutType.SectionDivider)
        {
            vm.Slide.PrimaryParagraph = PresEditPrimaryContentBox.Text;
        }
        else
        {
            var lines = PresEditPrimaryContentBox.Text.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            vm.Slide.PrimaryBullets = lines;
        }

        vm.NotifySlideChanged();
        RefreshLiveSlidePreview(vm.Slide);
        MarkPresentationDirty();
    }

    private void PresEditSecondaryContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_presUpdatingUiFromModel || _presSelectedSlideIndex < 0 || _presSelectedSlideIndex >= _presSlides.Count) return;
        var vm = _presSlides[_presSelectedSlideIndex];

        var lines = PresEditSecondaryContentBox.Text.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        vm.Slide.SecondaryBullets = lines;

        vm.NotifySlideChanged();
        RefreshLiveSlidePreview(vm.Slide);
        MarkPresentationDirty();
    }

    private void MarkPresentationDirty()
    {
        if (!_presHasUnsavedChanges)
        {
            _presHasUnsavedChanges = true;
            PresUnsavedBadge.Visibility = Visibility.Visible;
        }
    }

    // ============================================================
    // SLIDE OPERATIONS (ADD, DELETE, DUPLICATE, MOVE, SPLIT, MERGE, IMAGES)
    // ============================================================

    private void PresAddSlideDropdown_Click(object sender, RoutedEventArgs e)
    {
        var cm = new ContextMenu();
        void AddItem(string header, PresentationLayoutType layout)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, ev) =>
            {
                if (_presDeck is null) return;
                var slide = _presDeck.AddSlide(layout);
                var vm = new SlideItemViewModel(slide, _presSlides.Count + 1);
                _presSlides.Add(vm);
                PresSlideCountHeader.Text = $"SLIDES ({_presSlides.Count})";
                UpdateSlideMoveButtons();
                SelectSlide(_presSlides.Count - 1);
                MarkPresentationDirty();
            };
            cm.Items.Add(mi);
        }

        AddItem("Title Slide", PresentationLayoutType.Title);
        AddItem("Title and Content", PresentationLayoutType.TitleAndContent);
        AddItem("Section Divider", PresentationLayoutType.SectionDivider);
        AddItem("Two Column Content", PresentationLayoutType.TwoColumn);
        AddItem("Text with Image", PresentationLayoutType.TextWithImage);
        AddItem("Image Focused", PresentationLayoutType.ImageFocused);
        AddItem("Blank Slide", PresentationLayoutType.Blank);

        cm.PlacementTarget = sender as UIElement;
        cm.IsOpen = true;
    }

    private void PresSlideMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SlideItemViewModel item } && _presDeck is not null)
        {
            int idx = _presSlides.IndexOf(item);
            if (idx > 0)
            {
                _presDeck.MoveSlide(idx, idx - 1);
                _presSlides.Move(idx, idx - 1);
                RenumberSlides();
                SelectSlide(idx - 1);
                MarkPresentationDirty();
            }
        }
    }

    private void PresSlideMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SlideItemViewModel item } && _presDeck is not null)
        {
            int idx = _presSlides.IndexOf(item);
            if (idx >= 0 && idx < _presSlides.Count - 1)
            {
                _presDeck.MoveSlide(idx, idx + 1);
                _presSlides.Move(idx, idx + 1);
                RenumberSlides();
                SelectSlide(idx + 1);
                MarkPresentationDirty();
            }
        }
    }

    private void PresSlideDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SlideItemViewModel item } && _presDeck is not null)
        {
            int idx = _presSlides.IndexOf(item);
            var duplicated = _presDeck.DuplicateSlide(item.Slide.Id);
            if (duplicated is not null)
            {
                var vm = new SlideItemViewModel(duplicated, idx + 2);
                _presSlides.Insert(idx + 1, vm);
                RenumberSlides();
                SelectSlide(idx + 1);
                MarkPresentationDirty();
            }
        }
    }

    private void PresSlideDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SlideItemViewModel item } && _presDeck is not null)
        {
            int idx = _presSlides.IndexOf(item);
            _presDeck.DeleteSlide(item.Slide.Id);
            _presSlides.Remove(item);

            if (_presSlides.Count == 0 && _presDeck.Slides.Count > 0)
            {
                _presSlides.Add(new SlideItemViewModel(_presDeck.Slides[0], 1));
            }

            RenumberSlides();
            SelectSlide(Math.Clamp(idx, 0, _presSlides.Count - 1));
            MarkPresentationDirty();
        }
    }

    private void RenumberSlides()
    {
        for (int i = 0; i < _presSlides.Count; i++)
        {
            _presSlides[i].SlideNumber = i + 1;
        }
        UpdateSlideMoveButtons();
        PresSlideCountHeader.Text = $"SLIDES ({_presSlides.Count})";
    }

    private void UpdateSlideMoveButtons()
    {
        for (int i = 0; i < _presSlides.Count; i++)
        {
            _presSlides[i].CanMoveUp = i > 0;
            _presSlides[i].CanMoveDown = i < _presSlides.Count - 1;
        }
    }

    private void PresSplitSlide_Click(object sender, RoutedEventArgs e)
    {
        if (_presDeck is null || _presSelectedSlideIndex < 0 || _presSelectedSlideIndex >= _presSlides.Count) return;
        var currentVm = _presSlides[_presSelectedSlideIndex];
        var split = _presDeck.SplitSlide(currentVm.Slide.Id);
        if (split is not null)
        {
            var newVm = new SlideItemViewModel(split, _presSelectedSlideIndex + 2);
            _presSlides.Insert(_presSelectedSlideIndex + 1, newVm);
            RenumberSlides();
            currentVm.NotifySlideChanged();
            SelectSlide(_presSelectedSlideIndex + 1);
            MarkPresentationDirty();
            ShowBanner("Slide content split across two slides.", error: false);
        }
        else
        {
            ShowBanner("Slide does not have enough bullet points to split.", error: false, warning: true);
        }
    }

    private void PresMergeWithNext_Click(object sender, RoutedEventArgs e)
    {
        if (_presDeck is null || _presSelectedSlideIndex < 0 || _presSelectedSlideIndex >= _presSlides.Count - 1)
        {
            ShowBanner("No next slide available to merge with.", error: false, warning: true);
            return;
        }

        var currentVm = _presSlides[_presSelectedSlideIndex];
        if (_presDeck.MergeWithNext(currentVm.Slide.Id))
        {
            _presSlides.RemoveAt(_presSelectedSlideIndex + 1);
            RenumberSlides();
            currentVm.NotifySlideChanged();
            SelectSlide(_presSelectedSlideIndex);
            MarkPresentationDirty();
            ShowBanner("Merged with next slide successfully.", error: false);
        }
    }

    private void PresInsertImage_Click(object sender, RoutedEventArgs e)
    {
        if (_presSelectedSlideIndex < 0 || _presSelectedSlideIndex >= _presSlides.Count) return;
        var currentVm = _presSlides[_presSelectedSlideIndex];

        var ofd = new OpenFileDialog
        {
            Title = "Select Image for Slide",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|All Files (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            currentVm.Slide.Image = new SlideImageReference
            {
                ImagePath = ofd.FileName
            };

            if (currentVm.Slide.Layout == PresentationLayoutType.TitleAndContent)
            {
                currentVm.Slide.Layout = PresentationLayoutType.TextWithImage;
            }

            PresRemoveImageBtn.Visibility = Visibility.Visible;
            currentVm.NotifySlideChanged();
            UpdateLayoutButtons(currentVm.Slide.Layout);
            RefreshLiveSlidePreview(currentVm.Slide);
            MarkPresentationDirty();
        }
    }

    private void PresRemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (_presSelectedSlideIndex < 0 || _presSelectedSlideIndex >= _presSlides.Count) return;
        var currentVm = _presSlides[_presSelectedSlideIndex];
        currentVm.Slide.Image = null;
        if (currentVm.Slide.Layout is PresentationLayoutType.TextWithImage or PresentationLayoutType.ImageFocused)
        {
            currentVm.Slide.Layout = PresentationLayoutType.TitleAndContent;
        }

        PresRemoveImageBtn.Visibility = Visibility.Collapsed;
        currentVm.NotifySlideChanged();
        UpdateLayoutButtons(currentVm.Slide.Layout);
        RefreshLiveSlidePreview(currentVm.Slide);
        MarkPresentationDirty();
    }

    // ============================================================
    // LAYOUT, THEME, TYPOGRAPHY & RATIO LOGIC
    // ============================================================

    private void PresLayout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && Enum.TryParse<PresentationLayoutType>(btn.Tag?.ToString(), out var layout))
        {
            if (_presSelectedSlideIndex < 0 || _presSelectedSlideIndex >= _presSlides.Count) return;
            var currentVm = _presSlides[_presSelectedSlideIndex];
            currentVm.Slide.Layout = layout;
            currentVm.NotifySlideChanged();
            UpdateLayoutButtons(layout);
            SelectSlide(_presSelectedSlideIndex);
            MarkPresentationDirty();
        }
    }

    private void UpdateLayoutButtons(PresentationLayoutType layout)
    {
        void SetStyle(Button b, bool active) => b.Style = (Style)FindResource(active ? "PrimaryButton" : "SecondaryButton");
        SetStyle(PresLayoutTitleBtn, layout == PresentationLayoutType.Title);
        SetStyle(PresLayoutContentBtn, layout == PresentationLayoutType.TitleAndContent);
        SetStyle(PresLayoutDividerBtn, layout == PresentationLayoutType.SectionDivider);
        SetStyle(PresLayoutTwoColBtn, layout == PresentationLayoutType.TwoColumn);
        SetStyle(PresLayoutTextImgBtn, layout == PresentationLayoutType.TextWithImage);
        SetStyle(PresLayoutImgFocusBtn, layout == PresentationLayoutType.ImageFocused);
        SetStyle(PresLayoutBlankBtn, layout == PresentationLayoutType.Blank);
    }

    private void PresTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && _presDeck is not null)
        {
            var themeName = btn.Tag?.ToString();
            var theme = PresentationTheme.BuiltInThemes.FirstOrDefault(t => t.Name == themeName) ?? PresentationTheme.DarkSlate;
            _presDeck.Theme = theme;
            PresBgColorBox.Text = theme.BackgroundHex;
            PresAccentColorBox.Text = theme.AccentHex;

            UpdateAspectAndThemeButtons();
            if (_presSelectedSlideIndex >= 0 && _presSelectedSlideIndex < _presSlides.Count)
            {
                RefreshLiveSlidePreview(_presSlides[_presSelectedSlideIndex].Slide);
            }
            MarkPresentationDirty();
        }
    }

    private void UpdateAspectAndThemeButtons()
    {
        var theme = _presDeck?.Theme ?? PresentationTheme.DarkSlate;
        void SetThemeBtn(Button b, string name) => b.Style = (Style)FindResource(theme.Name == name ? "PrimaryButton" : "SecondaryButton");

        SetThemeBtn(PresThemeDarkSlateBtn, "Dark Slate");
        SetThemeBtn(PresThemeModernMinimalBtn, "Modern Minimal");
        SetThemeBtn(PresThemeClassicNavyBtn, "Classic Navy");
        SetThemeBtn(PresThemeVibrantSunsetBtn, "Vibrant Sunset");
        SetThemeBtn(PresThemeForestGreenBtn, "Forest Green");

        bool is16x9 = (_presDeck?.Dimensions.AspectRatio ?? SlideAspectRatio.Widescreen16x9) == SlideAspectRatio.Widescreen16x9;
        PresAspect16x9Btn.Style = (Style)FindResource(is16x9 ? "PrimaryButton" : "SecondaryButton");
        PresAspect4x3Btn.Style = (Style)FindResource(!is16x9 ? "PrimaryButton" : "SecondaryButton");

        // Resize preview box to exact aspect ratio
        if (is16x9)
        {
            PresSlidePreviewBox.Width = 640;
            PresSlidePreviewBox.Height = 360;
        }
        else
        {
            PresSlidePreviewBox.Width = 480;
            PresSlidePreviewBox.Height = 360;
        }
    }

    private void PresAspect16x9_Click(object sender, RoutedEventArgs e)
    {
        if (_presDeck is null) return;
        _presDeck.Dimensions = SlideDimensions.Widescreen16x9;
        UpdateAspectAndThemeButtons();
        MarkPresentationDirty();
    }

    private void PresAspect4x3_Click(object sender, RoutedEventArgs e)
    {
        if (_presDeck is null) return;
        _presDeck.Dimensions = SlideDimensions.Standard4x3;
        UpdateAspectAndThemeButtons();
        MarkPresentationDirty();
    }

    private void PresFontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_presUpdatingUiFromModel || _presDeck is null) return;
        if (PresFontFamilyCombo.SelectedItem is ComboBoxItem item)
        {
            var font = item.Content.ToString() ?? "Segoe UI";
            _presDeck.Theme = _presDeck.Theme with { FontFamily = font };
            if (_presSelectedSlideIndex >= 0 && _presSelectedSlideIndex < _presSlides.Count)
            {
                RefreshLiveSlidePreview(_presSlides[_presSelectedSlideIndex].Slide);
            }
            MarkPresentationDirty();
        }
    }

    private void PresTitleSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_presUpdatingUiFromModel || _presDeck is null) return;
        _presDeck.Theme = _presDeck.Theme with { TitleFontSize = PresTitleSizeSlider.Value };
        if (_presSelectedSlideIndex >= 0 && _presSelectedSlideIndex < _presSlides.Count)
        {
            RefreshLiveSlidePreview(_presSlides[_presSelectedSlideIndex].Slide);
        }
        MarkPresentationDirty();
    }

    private void PresBodySizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_presUpdatingUiFromModel || _presDeck is null) return;
        _presDeck.Theme = _presDeck.Theme with { BodyFontSize = PresBodySizeSlider.Value };
        if (_presSelectedSlideIndex >= 0 && _presSelectedSlideIndex < _presSlides.Count)
        {
            RefreshLiveSlidePreview(_presSlides[_presSelectedSlideIndex].Slide);
        }
        MarkPresentationDirty();
    }

    private void PresAlignLeft_Click(object sender, RoutedEventArgs e)
    {
        if (_presDeck is null) return;
        _presDeck.Theme = _presDeck.Theme with { Alignment = SlideTextAlignment.Left };
        PresAlignLeftBtn.Style = (Style)FindResource("PrimaryButton");
        PresAlignCenterBtn.Style = (Style)FindResource("SecondaryButton");
        if (_presSelectedSlideIndex >= 0 && _presSelectedSlideIndex < _presSlides.Count)
        {
            RefreshLiveSlidePreview(_presSlides[_presSelectedSlideIndex].Slide);
        }
        MarkPresentationDirty();
    }

    private void PresAlignCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_presDeck is null) return;
        _presDeck.Theme = _presDeck.Theme with { Alignment = SlideTextAlignment.Center };
        PresAlignLeftBtn.Style = (Style)FindResource("SecondaryButton");
        PresAlignCenterBtn.Style = (Style)FindResource("PrimaryButton");
        if (_presSelectedSlideIndex >= 0 && _presSelectedSlideIndex < _presSlides.Count)
        {
            RefreshLiveSlidePreview(_presSlides[_presSelectedSlideIndex].Slide);
        }
        MarkPresentationDirty();
    }

    private void PresColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_presUpdatingUiFromModel || _presDeck is null) return;
        var bg = PresBgColorBox.Text.Trim();
        var accent = PresAccentColorBox.Text.Trim();

        if (bg.Length >= 4 && bg.StartsWith('#'))
        {
            _presDeck.Theme = _presDeck.Theme with { BackgroundHex = bg };
        }
        if (accent.Length >= 4 && accent.StartsWith('#'))
        {
            _presDeck.Theme = _presDeck.Theme with { AccentHex = accent };
        }

        if (_presSelectedSlideIndex >= 0 && _presSelectedSlideIndex < _presSlides.Count)
        {
            RefreshLiveSlidePreview(_presSlides[_presSelectedSlideIndex].Slide);
        }
        MarkPresentationDirty();
    }

    // ============================================================
    // PPTX EXPORT & RESULTS
    // ============================================================

    private async void PresExportPptx_Click(object sender, RoutedEventArgs e)
    {
        if (_isExportingPresentation || _presDeck is null) return;

        var validationErrors = _presDeck.Validate();
        if (validationErrors.Count > 0)
        {
            ShowBanner($"Cannot export: {string.Join(" ", validationErrors)}", error: true);
            return;
        }

        _isExportingPresentation = true;
        PresExportPptxBtn.IsEnabled = false;
        PresProgressBlock.Visibility = Visibility.Visible;
        PresResultCard.Visibility = Visibility.Collapsed;
        PresProgressBar.Value = 0;
        PresProgressStatusText.Text = "Initializing presentation export…";

        _presExportCts = new CancellationTokenSource();
        var token = _presExportCts.Token;

        var progress = new Progress<ProcessingProgress>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                PresProgressBar.Value = p.Percent;
                PresProgressStatusText.Text = p.Message;
            });
        });

        try
        {
            var customName = PresDeckTitleBox.Text.Trim();
            var deck = _presDeck;

            var outputPath = await App.Services.PresentationStudio.ExportPresentationAsync(
                deck,
                customName,
                progress,
                token).ConfigureAwait(true);

            _lastGeneratedPresentationPath = outputPath;
            _presHasUnsavedChanges = false;
            PresUnsavedBadge.Visibility = Visibility.Collapsed;

            PresResultPathText.Text = outputPath;
            PresResultCard.Visibility = Visibility.Visible;
            ShowBanner($"Presentation exported to {Path.GetFileName(outputPath)}", error: false);
        }
        catch (OperationCanceledException)
        {
            ShowBanner("Presentation export was cancelled.", error: false, warning: true);
        }
        catch (Exception ex)
        {
            App.Logger.Error("pres", "Failed to export presentation", ex);
            ShowBanner($"Presentation export failed: {ex.Message}", error: true);
        }
        finally
        {
            _isExportingPresentation = false;
            PresExportPptxBtn.IsEnabled = true;
            PresProgressBlock.Visibility = Visibility.Collapsed;
            _presExportCts?.Dispose();
            _presExportCts = null;
        }
    }

    private void PresCancelExport_Click(object sender, RoutedEventArgs e)
    {
        _presExportCts?.Cancel();
    }

    private void PresOpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastGeneratedPresentationPath) && File.Exists(_lastGeneratedPresentationPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastGeneratedPresentationPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowBanner($"Could not open presentation: {ex.Message}", error: true);
            }
        }
    }

    private void PresRevealResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastGeneratedPresentationPath) && File.Exists(_lastGeneratedPresentationPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_lastGeneratedPresentationPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowBanner($"Could not show file in folder: {ex.Message}", error: true);
            }
        }
    }

    private void PresDismissResult_Click(object sender, RoutedEventArgs e)
    {
        PresResultCard.Visibility = Visibility.Collapsed;
    }
}
