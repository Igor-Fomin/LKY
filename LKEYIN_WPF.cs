#pragma warning disable CA1416 // Suppress Audio warning
#pragma warning disable CS8618 // Suppress Non-nullable field warnings
#pragma warning disable CS8600 // Suppress Null conversion warnings
#pragma warning disable CS8601 // Suppress Null assignment warnings

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Xml.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Data;
using System.Text.RegularExpressions;
using System.Globalization;

// AutoCAD Namespaces
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;

[assembly: CommandClass(typeof(CadastreTools.LKeyinCommand))]

namespace CadastreTools;

#region 1. GLOBAL CONSTANTS
public static class CadConstants
{
    public const string BDY_BEARING = "BDY_BEARING";
    public const string BDY_DISTANCE = "BDY_DISTANCE";
    public const string CONNECTION_BEAR = "CONNECTION_BEAR";
    public const string CONNECTION_DIST = "CONNECTION_DIST";
    public const string SYMB_TEXT = "SYMB TEXT";
    public const string POINT_NUMBER = "POINT_NUMBER";
    public const string VAR_PT_COUNTER = "CADASTRE_PT_NUM";

    // 1:1000 Reference Heights (Model Space mm)
    public static readonly Dictionary<string, double> LayerHeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        { BDY_BEARING, 3.0 },
        { BDY_DISTANCE, 3.0 },
        { CONNECTION_BEAR, 2.5 },
        { CONNECTION_DIST, 2.5 },
        { POINT_NUMBER, 2.5 },
        { SYMB_TEXT, 2.5 },
        { "BEAR", 2.0 },
        { "DIM", 2.0 },
        { "STNO", 1.8 },
        { "CORINF", 1.6 }
    };

    public static double GetReferenceHeight(string layerName)
    {
        if (LayerHeights.TryGetValue(layerName, out double h)) return h;
        return 2.0; // Default fallback
    }
}
#endregion

#region 2. SETTINGS
public class TextSettings
{
    public string Style { get; set; } = "Standard";
    public double Size { get; set; } = 1.0;
    public bool IsMText { get; set; } = false;
    public bool Masking { get; set; } = false;
    public short ColorIndex { get; set; } = 256; // Forced ByLayer
    public bool Visible { get; set; } = true;

    public void Reset(short unused, string defaultStyle = "Standard")
    {
        Style = defaultStyle;
        Size = 1.0;
        IsMText = false;
        Masking = false;
        ColorIndex = 256; // Forced ByLayer
        Visible = true;
    }
}

public class AppSettings
{
    public bool AudioFeedback { get; set; } = true;
    public string AudioSound { get; set; } = "Asterisk";
    public double SnapTolerance { get; set; } = 0.005;

    public TextSettings TextBrg { get; set; } = new TextSettings() { ColorIndex = 256 };
    public TextSettings TextDist { get; set; } = new TextSettings() { ColorIndex = 256 };
    public TextSettings TextPt { get; set; } = new TextSettings() { ColorIndex = 256, Style = "ROMAND140" };
    public TextSettings TextComm { get; set; } = new TextSettings() { ColorIndex = 256, Style = "ROMANS80" };

    public static void Save(AppSettings settings)
    {
        try
        {
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadastreTools");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, "CadastreKeyinSettings.xml");
            XmlSerializer xs = new XmlSerializer(typeof(AppSettings));
            using (StreamWriter wr = new StreamWriter(path)) { xs.Serialize(wr, settings); }
        }
        catch (System.Exception ex) { MessageBox.Show("Error saving settings: " + ex.Message); }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadastreTools", "CadastreKeyinSettings.xml");
            if (File.Exists(path))
            {
                XmlSerializer xs = new XmlSerializer(typeof(AppSettings));
                using (StreamReader rd = new StreamReader(path)) { return (AppSettings)xs.Deserialize(rd) ?? new AppSettings(); }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void ResetText()
    {
        TextBrg.Reset(256);
        TextDist.Reset(256);
        TextPt.Reset(256, "ROMAND140");
        TextComm.Reset(256, "ROMANS80");
    }
}
#endregion

#region 3. UI THEME
public static class UITheme
{
    public static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
    public static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(45, 45, 48));
    public static readonly Brush InputBackground = new SolidColorBrush(Color.FromRgb(20, 20, 20));
    public static readonly Brush AccentColor = new SolidColorBrush(Color.FromRgb(0, 122, 204));
    public static readonly Brush ActionBlue = new SolidColorBrush(Color.FromRgb(41, 128, 185));
    public static readonly Brush GuideColor = new SolidColorBrush(Color.FromRgb(0, 200, 0));
    
    private static readonly DropShadowEffect CardShadow = new DropShadowEffect() { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.4 };
    private static readonly FontFamily MonoFont = new FontFamily("Consolas");

    public static Border CreateCard() { return new Border() { Background = CardBrush, CornerRadius = new CornerRadius(4), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), Effect = CardShadow }; }
    public static TextBox CreateInputBox() { return new TextBox() { Background = InputBackground, Foreground = Brushes.Cyan, FontFamily = MonoFont, FontSize = 13, Height = 32, VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Padding = new Thickness(5), CaretBrush = Brushes.White }; }
    public static ComboBox CreateLayerCombo() { return new ComboBox() { Height = 28, Margin = new Thickness(2), IsEditable = true, Foreground = Brushes.Black, FontSize = 11 }; }
    public static Label CreateLabel(string text) { return new Label() { Content = text, Foreground = Brushes.LightGray, FontSize = 10, FontWeight = FontWeights.Bold, Padding = new Thickness(0, 4, 0, 1) }; }
    public static TextBlock CreateFooterText(string text, Brush color) { return new TextBlock() { Text = text, Foreground = color, FontSize = 9, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2) }; }
 
    public static Button CreateLayerBtn(string key) { return new Button() { Content = key, Height = 36, Margin = new Thickness(2), FontWeight = FontWeights.Bold, FontSize = 11, BorderThickness = new Thickness(0), Foreground = Brushes.White }; }
    public static CheckBox CreateToggle(string text) { return new CheckBox() { Content = text, Foreground = Brushes.White, Margin = new Thickness(4), FontSize = 12 }; }
    public static Button CreateActionBtn(string text, Brush bg) { return new Button() { Content = text, Height = 32, Background = bg, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 10.5, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch }; }

    public static UIElement CreateShortcutContent(string key, string description)
    {
        StackPanel sp = new StackPanel() { VerticalAlignment = VerticalAlignment.Center };
        
        TextBlock tbKey = new TextBlock() { 
            Text = key.ToUpper(), 
            FontSize = 8.0, 
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            HorizontalAlignment = HorizontalAlignment.Center 
        };
        
        TextBlock tbDesc = new TextBlock() { 
            Text = description, 
            FontSize = 10.5, 
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0) 
        };
        
        sp.Children.Add(tbKey);
        sp.Children.Add(tbDesc);
        return sp;
    }

    public static Button CreateColorBtn(short colorIndex)
    {
        Button b = new Button() { Width = 40, Height = 25, Margin = new Thickness(2) };
        if (colorIndex == 256) b.Content = "ByL";
        else if (colorIndex == 0) b.Content = "ByB";
        else b.Background = new SolidColorBrush(GetWpfColor(colorIndex));
        return b;
    }
    public static Color GetWpfColor(short index) { try { var acCol = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, index); return Color.FromRgb(acCol.ColorValue.R, acCol.ColorValue.G, acCol.ColorValue.B); } catch { return Colors.Gray; } }
}
#endregion

#region 4. COMMAND & MATH
public class LKeyinCommand
{
    private static PaletteSet? _paletteSet = null;
    private static CadastreWpfWindow? _control = null;

    [CommandMethod("LKY", CommandFlags.Session)]
    public void RunLKeyin()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        if (_paletteSet == null)
        {
            _paletteSet = new PaletteSet("Cadastre Tools", new Guid("D7CE4893-6F1D-4A9B-B85E-37F15002C9FA"));
            _paletteSet.Size = new System.Drawing.Size(320, 700);
            _paletteSet.DockEnabled = DockSides.Left | DockSides.Right;
            _paletteSet.Dock = DockSides.Right;
            
            _control = new CadastreWpfWindow(doc);
            _paletteSet.AddVisual("Cadastre Lines", _control);
        }

        _paletteSet.Visible = true;
    }
}

public static class CadMath
{
    public static double ParseDmsToDegrees(double rawInput)
    {
        // rawInput is in DDD.MMSS format (e.g. 10.00155 for 10°00'15.5")
        int d = (int)rawInput;
        double frac = Math.Round((rawInput - d) * 10000, 6);
        int m = (int)(Math.Round(frac, 6) / 100);
        double s = Math.Round(frac - (m * 100), 6);

        if (d >= 360 || m >= 60 || s >= 60)
        {
            System.Windows.MessageBox.Show("Degrees < 360, Minutes < 60, and Seconds < 60 required.", "Invalid Bearing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return -1;
        }

        return d + (m / 60.0) + (s / 3600.0);
    }

    public static string DegreesToDmsString(double decimalDegrees)
    {
        decimalDegrees = decimalDegrees % 360;
        if (decimalDegrees < 0) decimalDegrees += 360;

        int d = (int)decimalDegrees;
        double remainder = (decimalDegrees - d) * 60.0;
        int m = (int)remainder;
        double s = Math.Round((remainder - m) * 60.0);
        
        if (s >= 60) { s = 0; m++; }
        if (m >= 60) { m = 0; d++; }
        d = d % 360;

        return $"{d}.{m:00}{s:00}";
    }

    public static string DmsToString(double dmsValue) { return dmsValue.ToString("0.0000"); }

    public static string FormatAsSurveyor(double dmsValue)
    {
        int d = (int)dmsValue;
        int mmss = (int)Math.Round((dmsValue - d) * 10000);
        int m = mmss / 100;
        int s = mmss % 100;
        return $"{d}\u00B0{m:00}'{s:00}\"";
    }

    public static double AddSubDms(double dms1, double dms2, bool add)
    {
        double deg1 = ParseDmsToDegrees(dms1); double deg2 = ParseDmsToDegrees(dms2);
        double resDeg = add ? (deg1 + deg2) : (deg1 - deg2);
        return double.Parse(DegreesToDmsString(resDeg));
    }

    public static bool TryParseBearing(string input, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        // Clean input: handle labels with symbols (e.g., 120°29'00") by converting to DDD.MMSS format
        string cleaned = input.Replace("\u00B0", ".").Replace("°", ".").Replace("'", "").Replace("\"", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[^0-9. ]", "");
        input = cleaned.Trim();

        string normalizedInput = input;
        bool isValidFormat = false;

        // 1. Whole Degrees (e.g. "10")
        if (Regex.IsMatch(input, @"^\d+$"))
        {
            isValidFormat = true;
        }
        // 2. Strict formats: DDD.MMSS
        else if (Regex.IsMatch(input, @"^\d+\.\d+$"))
        {
            isValidFormat = true;
        }
        // 3. Strict formats: DDD MMSS
        else if (Regex.IsMatch(input, @"^\d+ \d+$"))
        {
            normalizedInput = input.Replace(" ", ".");
            isValidFormat = true;
        }

        if (!isValidFormat || !double.TryParse(normalizedInput, out result)) return false;

        string[] parts = normalizedInput.Split('.');
        if (parts.Length > 0 && int.TryParse(parts[0], out int d))
        {
            if (d >= 360 || d < 0)
            {
                System.Windows.MessageBox.Show("Degrees must be between 0 and 359.", "Invalid Bearing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        if (parts.Length > 1)
        {
            string frac = parts[1].PadRight(4, '0');
            string mStr = frac.Substring(0, 2);
            string sStr = frac.Substring(2, 2);

            if (int.TryParse(mStr, out int m) && m >= 60)
            {
                System.Windows.MessageBox.Show($"Invalid Minutes: {mStr}. Survey format (DDD.MMSS) requires Minutes < 60.", "Invalid Bearing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (int.TryParse(sStr, out int s) && s >= 60)
            {
                System.Windows.MessageBox.Show($"Invalid Seconds: {sStr}. Survey format (DDD.MMSS) requires Seconds < 60.", "Invalid Bearing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }
}
#endregion

#region 5. DATA MANAGER
public static class DwgDataManager
{
    public static int GetNextPointNumber()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return 1;
        using (DocumentLock loc = doc.LockDocument())
        using (Transaction tr = doc.TransactionManager.StartTransaction())
        {
            int num = GetNextPointNumber(tr, doc.Database);
            tr.Commit();
            return num;
        }
    }

    public static int GetNextPointNumber(Transaction tr, Database db)
    {
        int nextNum = 1;
        DatabaseSummaryInfoBuilder infoBuilder = new DatabaseSummaryInfoBuilder(db.SummaryInfo);
        bool found = false;

        if (infoBuilder.CustomPropertyTable.Contains(CadConstants.VAR_PT_COUNTER))
        {
            string val = (string)infoBuilder.CustomPropertyTable[CadConstants.VAR_PT_COUNTER];
            if (int.TryParse(val, out int storedNum)) { nextNum = storedNum; found = true; }
        }
        if (!found)
        {
            nextNum = ScanLayerForMaxPoint(db, tr) + 1;
            SetNextPointNumber(nextNum, tr, db);
        }
        return nextNum;
    }

    public static void SetNextPointNumber(int num)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        using (DocumentLock loc = doc.LockDocument())
        using (Transaction tr = doc.TransactionManager.StartTransaction())
        {
            SetNextPointNumber(num, tr, doc.Database);
            tr.Commit();
        }
    }

    public static void SetNextPointNumber(int num, Transaction tr, Database db)
    {
        DatabaseSummaryInfoBuilder infoBuilder = new DatabaseSummaryInfoBuilder(db.SummaryInfo);
        if (infoBuilder.CustomPropertyTable.Contains(CadConstants.VAR_PT_COUNTER)) infoBuilder.CustomPropertyTable[CadConstants.VAR_PT_COUNTER] = num.ToString();
        else infoBuilder.CustomPropertyTable.Add(CadConstants.VAR_PT_COUNTER, num.ToString());
        db.SummaryInfo = infoBuilder.ToDatabaseSummaryInfo();
    }

    private static int ScanLayerForMaxPoint(Database db, Transaction tr)
    {
        int max = 0;
        try
        {
            Editor ed = AcApp.DocumentManager.MdiActiveDocument.Editor;
            TypedValue[] filter = new TypedValue[]
            {
                new TypedValue((int)DxfCode.LayerName, CadConstants.POINT_NUMBER),
                new TypedValue((int)DxfCode.Operator, "<OR"),
                new TypedValue((int)DxfCode.Start, "TEXT"),
                new TypedValue((int)DxfCode.Start, "MTEXT"),
                new TypedValue((int)DxfCode.Operator, "OR>")
            };
            PromptSelectionResult res = ed.SelectAll(new SelectionFilter(filter));
            if (res.Status == PromptStatus.OK)
            {
                foreach (ObjectId id in res.Value.GetObjectIds())
                {
                    Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    string txt = "";
                    if (ent is DBText dbt) txt = dbt.TextString; else if (ent is MText mt) txt = mt.Contents;
                    if (int.TryParse(txt, out int val)) { if (val > max) max = val; }
                }
            }
        }
        catch { }
        return max;
    }

    public static bool IsPointNumberAtLocation(Point3d pt, Transaction tr, Database db)
    {
        try
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(CadConstants.POINT_NUMBER)) return false;

            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                if (ent.Layer == CadConstants.POINT_NUMBER)
                {
                    Point3d txtPos = Point3d.Origin;
                    if (ent is DBText dbt) txtPos = dbt.Position;
                    else if (ent is MText mt) txtPos = mt.Location;

                    if (txtPos.DistanceTo(pt) < 0.01) return true;
                }
            }
        }
        catch { }
        return false;
    }
}
#endregion

#region 6. MAIN WINDOW
public class CadastreWpfWindow : System.Windows.Controls.UserControl
{
    #region Properties & State
    private struct LayerDef { public string Name; public short Color; public string Linetype; public double LinetypeScale; }
    private static readonly Dictionary<Key, LayerDef> LayerConfig = new Dictionary<Key, LayerDef>
    {
        { Key.Q, new LayerDef { Name = "BOUNDARY_SUBJECT", Color = 4, Linetype = "Continuous", LinetypeScale = 1.0 } },
        { Key.W, new LayerDef { Name = "BOUNDARY_ADJOINING", Color = 2, Linetype = "Continuous", LinetypeScale = 1.0 } },
        { Key.E, new LayerDef { Name = "CONNECTIONS", Color = 1, Linetype = "DASHED", LinetypeScale = 0.3 } },
        { Key.A, new LayerDef { Name = "BDY_EASEMENT", Color = 2, Linetype = "DASHED", LinetypeScale = 0.5 } },
        { Key.S, new LayerDef { Name = "ADDITIONAL_1", Color = 6, Linetype = "Continuous", LinetypeScale = 1.0 } },
        { Key.D, new LayerDef { Name = "ADDITIONAL_2", Color = 3, Linetype = "Continuous", LinetypeScale = 1.0 } }
    };

    private Document _doc;
    private Point3d _currentPoint;
    private Point3d _lastCreatedVertex;
    private bool _hasStartPoint = false;
    private Stack<List<ObjectId>> _undoStack = new Stack<List<ObjectId>>();
    private List<Point3d> _traversePath = new List<Point3d>();
    private AppSettings _config;
    private string _currentLayer = "BOUNDARY_SUBJECT";
    private bool _isBusy = false;
    private bool _isSyncingScale = false;
    private bool _feetMode = false;
    private bool _distanceIsConverted = false;
    private double _plotScale = 1000.0;
    private double _prevPlotScale;
    private double GetModelSize(double paperSize) => paperSize * (_plotScale / 1000.0);

    private struct TraverseSegment
    {
        public int Index;
        public string Bearing;
        public string Distance;
        public string Layer;
        public Point3d EndPoint;
    }
    private List<TraverseSegment> _segmentHistory = new List<TraverseSegment>();
    private ScrollViewer svHistory = null!;
    private StackPanel pnlHistory = null!;
    private TextBlock lblStats = null!;

    // Controls
    private TextBox txtBearing = null!, txtDistance = null!, txtScale = null!;
    private TextBlock lblBearingTrace = null!, lblDistanceTrace = null!;
    private Button btnSound = null!;

    // Buttons
    private Button btnQ = null!, btnW = null!, btnE = null!, btnA = null!, btnS = null!, btnD = null!;

    private bool EnsureQuiescent()
    {
        if (!_doc.Editor.IsQuiescent)
        {
            _doc.Editor.WriteMessage("\n[BUSY] Please press ESC in AutoCAD before using the tool.");
            return false;
        }
        return true;
    }
    #endregion

    #region Constructor & Cleanup
    public CadastreWpfWindow(Document doc)
    {
        _doc = doc;
        _config = AppSettings.Load();

        try
        {
            Database db = HostApplicationServices.WorkingDatabase;
            AnnotationScale scale = db.Cannoscale;
            double factor = (scale.DrawingUnits / scale.PaperUnits) * 1000.0;

            if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor))
            {
                _plotScale = 1000.0;
            }
            else
            {
                _plotScale = factor;
            }
            _prevPlotScale = _plotScale;
            _doc.Editor.WriteMessage($"\nDEBUG: Detected Annotation Scale is 1:{_plotScale}");
        }
        catch
        {
            _plotScale = 1000.0;
            _prevPlotScale = 1000.0;
        }

        _doc.Database.SystemVariableChanged += Database_SystemVariableChanged;

        InitializeCustomUI();
        InitializeProjectLayers();
        UpdateLayerButtons();

        // Default to 'W' layer def from config
        _currentLayer = LayerConfig[Key.W].Name;
        HighlightActiveLayer(btnW);
    }

    private double GetScaleFactor(AnnotationScale scale)
    {
        // For Meters drawings, 1:300 is defined as 0.3 DrawingUnits to 1 PaperUnit.
        // Multiplying by 1000.0 yields the intuitive '300' factor used in the UI.
        return Math.Round((scale.DrawingUnits / scale.PaperUnits) * 1000.0);
    }

    private void Database_SystemVariableChanged(object sender, Autodesk.AutoCAD.DatabaseServices.SystemVariableChangedEventArgs e)
    {
        if (e.Name == "CANNOSCALE" && !_isSyncingScale)
        {
            try
            {
                Database db = _doc.Database;
                double newScale = GetScaleFactor(db.Cannoscale); // Task 3: CAD -> Plugin Sync
                
                if (Math.Abs(_plotScale - newScale) > 1e-6)
                {
                    _plotScale = newScale;
                    this.Dispatcher.BeginInvoke(new Action(() => {
                        if (txtScale != null) txtScale.Text = _plotScale.ToString("0"); // Integer representation
                        ScaleAllText();
                    }));
                }
            }
            catch { }
        }
    }

    private void SetCadAnnotativeScale(double targetScale)
    {
        ExecuteUiAction(() => {
            _isSyncingScale = true;
            try
            {
                using (DocumentLock loc = _doc.LockDocument())
                using (Transaction tr = _doc.TransactionManager.StartTransaction())
                {
                    Database db = _doc.Database;
                    ObjectContextManager manager = db.ObjectContextManager;
                    ObjectContextCollection collection = manager.GetContextCollection("ACDB_ANNOTATIONSCALES");

                    string scaleName = $"1:{targetScale}";
                    ObjectContext foundContext = collection.GetContext(scaleName);

                    if (foundContext != null)
                    {
                        db.Cannoscale = (AnnotationScale)foundContext;
                        tr.Commit();
                    }
                    else
                    {
                        _doc.Editor.WriteMessage($"\n[Warning] Scale '{scaleName}' not found in drawing scale list.");
                    }
                }
            }
            finally
            {
                _isSyncingScale = false;
            }
        });
    }

    private bool ValidateScaleExists(double targetScale)
    {
        try
        {
            Database db = _doc.Database;
            ObjectContextManager manager = db.ObjectContextManager;
            ObjectContextCollection collection = manager.GetContextCollection("ACDB_ANNOTATIONSCALES");
            return collection.GetContext($"1:{targetScale}") != null;
        }
        catch { return false; }
    }

    private void CadastreWpfWindow_Closed(object? sender, EventArgs e)
    {
        _doc.Database.SystemVariableChanged -= Database_SystemVariableChanged;
    }
    #endregion

    #region Centralized Action Handling
    private void ExecuteUiAction(Action action)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            action();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            _doc.Editor.WriteMessage($"\n[AutoCAD Error] {ex.Message}");
        }
        catch (System.Exception ex)
        {
            _doc.Editor.WriteMessage($"\n[System Error] {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        if (txtBearing != null) txtBearing.IsEnabled = !busy;
        if (txtDistance != null) txtDistance.IsEnabled = !busy;
        
        if (busy)
        {
            System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.WaitCursor;
        }
        else
        {
            System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.Default;
        }
    }
    #endregion

    #region Initialization
    private void InitializeProjectLayers()
    {
        try
        {
            using (DocumentLock loc = _doc.LockDocument())
            using (Transaction tr = _doc.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(_doc.Database.LayerTableId, OpenMode.ForRead);
                LinetypeTable ltt = (LinetypeTable)tr.GetObject(_doc.Database.LinetypeTableId, OpenMode.ForRead);

                foreach (var entry in LayerConfig.Values)
                {
                    if (!lt.Has(entry.Name))
                    {
                        lt.UpgradeOpen();
                        LayerTableRecord ltr = new LayerTableRecord();
                        ltr.Name = entry.Name;
                        ltr.Color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, entry.Color);

                        if (entry.Linetype != "Continuous")
                        {
                            if (!ltt.Has(entry.Linetype))
                            {
                                try { _doc.Database.LoadLineTypeFile(entry.Linetype, "acad.lin"); }
                                catch { _doc.Editor.WriteMessage($"\n[Error] Could not load linetype {entry.Linetype}. Ensure acad.lin is in search path."); }
                            }
                            if (ltt.Has(entry.Linetype)) ltr.LinetypeObjectId = ltt[entry.Linetype];
                        }

                        lt.Add(ltr);
                        tr.AddNewlyCreatedDBObject(ltr, true);
                    }
                }

                // Automated Layer Setup for Text Layers
                EnsureLayer(lt, ltt, CadConstants.BDY_DISTANCE, 2, tr); // Yellow
                EnsureLayer(lt, ltt, CadConstants.BDY_BEARING, 2, tr);  // Yellow
                EnsureLayer(lt, ltt, CadConstants.CONNECTION_DIST, 1, tr); // Red
                EnsureLayer(lt, ltt, CadConstants.CONNECTION_BEAR, 1, tr); // Red
                EnsureLayer(lt, ltt, CadConstants.SYMB_TEXT, 1, tr);       // Red
                EnsureLayer(lt, ltt, CadConstants.POINT_NUMBER, 3, tr);    // Green

                // Style Availability Check
                string[] mandatoryStyles = { "STENDOT100", "STENDOT100S", "STENDOT80", "ROMANS80", "ROMAND140" };
                foreach (var s in mandatoryStyles) GetTextStyleId(tr, s, _doc.Database);

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            _doc.Editor.WriteMessage($"\n[Critical] Layer initialization failed: {ex.Message}");
        }
    }

    private void EnsureLayer(LayerTable lt, LinetypeTable ltt, string name, short colorIndex, Transaction tr, string linetype = "Continuous")
    {
        if (!lt.Has(name))
        {
            lt.UpgradeOpen();
            LayerTableRecord ltr = new LayerTableRecord();
            ltr.Name = name;
            ltr.Color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);

            if (ltt.Has(linetype))
            {
                ltr.LinetypeObjectId = ltt[linetype];
            }
            else if (linetype != "Continuous")
            {
                _doc.Editor.WriteMessage($"\n[Warning] Linetype '{linetype}' not found. Defaulting to Continuous for layer '{name}'.");
                ltr.LinetypeObjectId = ltt["Continuous"];
            }

            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }
    }

    private void InitializeCustomUI()
    {
        this.Background = UITheme.BackgroundBrush;

        Grid mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 0: Header Icons
        mainGrid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) }); // 1: Main Content

        // Header Icons
        UIElement headerIcons = BuildHeaderIcons();
        Grid.SetRow(headerIcons, 0); mainGrid.Children.Add(headerIcons);

        // Main Content (Directly Input Tab)
        object inputContent = BuildInputTab();
        if (inputContent is UIElement uiContent)
        {
            Grid.SetRow(uiContent, 1); mainGrid.Children.Add(uiContent);
        }

        this.Content = mainGrid;
        this.PreviewKeyDown += Window_PreviewKeyDown;
        UpdateSoundIcon();

        this.Loaded += (s, e) => 
        {
            if (txtBearing != null) 
            {
                txtBearing.Focus(); 
                txtBearing.SelectAll();
            }
        };
    }    private UIElement BuildHeaderIcons()
    {
        StackPanel sp = new StackPanel() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 5, 3), VerticalAlignment = VerticalAlignment.Center };
        
        // Plot Scale Input
        StackPanel spScale = new StackPanel() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        Label lblScale = UITheme.CreateLabel("SCALE 1:");
        lblScale.VerticalAlignment = VerticalAlignment.Center;
        lblScale.Margin = new Thickness(0, 0, 2, 0);
        spScale.Children.Add(lblScale);
        txtScale = new TextBox() { Text = _plotScale.ToString("G"), Width = 40, Height = 22, Background = UITheme.InputBackground, Foreground = Brushes.Cyan, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "Enter target plot scale (e.g., 500 for 1:500) and press ENTER to update drawing." };
        txtScale.TextChanged += (s, e) => { 
            if (double.TryParse(txtScale.Text, out double val) && val > 0) 
            {
                if (ValidateScaleExists(val))
                {
                    txtScale.Foreground = Brushes.Cyan;
                    _plotScale = val;
                }
                else
                {
                    txtScale.Foreground = Brushes.OrangeRed;
                }
            }
            else if (string.IsNullOrWhiteSpace(txtScale.Text)) _plotScale = 1000.0;
        };
        txtScale.KeyDown += (s, e) => {
            if (e.Key == Key.Enter) {
                if (double.TryParse(txtScale.Text, out double val) && val > 0) {
                    if (ValidateScaleExists(val))
                    {
                        _isSyncingScale = true;
                        try {
                            _plotScale = val;
                            SetCadAnnotativeScale(val);
                            ScaleAllText();
                            _doc.Editor.Regen();
                            txtBearing.Focus();
                            txtBearing.SelectAll();
                        } finally {
                            _isSyncingScale = false;
                        }
                    }
                    else
                    {
                        _doc.Editor.WriteMessage($"\n[Error] Scale '1:{val}' is not defined in this drawing.");
                        System.Media.SystemSounds.Exclamation.Play();
                    }
                }
            }
        };        spScale.Children.Add(txtScale);
 
        // Visibility Toggles
        Button btnTglBrg = new Button() { Content = "\u2221", Width = 28, Height = 28, FontSize = 14, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.Yellow, ToolTip = "Toggle Bearing Visibility", Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        Button btnTglDist = new Button() { Content = "\u2194", Width = 28, Height = 28, FontSize = 14, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.Yellow, ToolTip = "Toggle Distance Visibility", Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        Button btnTglPt = new Button() { Content = "\u2316", Width = 28, Height = 28, FontSize = 14, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.Lime, ToolTip = "Toggle Point Number Visibility", Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        Button btnTglComm = new Button() { Content = "\ud83d\udcac", Width = 28, Height = 28, FontSize = 14, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.Red, ToolTip = "Toggle Comment Visibility", Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
 
        UpdateToggleStyle(btnTglBrg, _config.TextBrg.Visible);
        UpdateToggleStyle(btnTglDist, _config.TextDist.Visible);
        UpdateToggleStyle(btnTglPt, _config.TextPt.Visible);
        UpdateToggleStyle(btnTglComm, _config.TextComm.Visible);
 
        btnTglBrg.Click += (s, e) => {
            _config.TextBrg.Visible = !_config.TextBrg.Visible;
            ToggleLayerVisibility(CadConstants.BDY_BEARING, _config.TextBrg.Visible);
            ToggleLayerVisibility(CadConstants.CONNECTION_BEAR, _config.TextBrg.Visible);
            ToggleLayerVisibility("BEAR", _config.TextBrg.Visible);
            UpdateToggleStyle(btnTglBrg, _config.TextBrg.Visible);
            AppSettings.Save(_config);
            _doc.Editor.Regen();
        };
        btnTglDist.Click += (s, e) => {
            _config.TextDist.Visible = !_config.TextDist.Visible;
            ToggleLayerVisibility(CadConstants.BDY_DISTANCE, _config.TextDist.Visible);
            ToggleLayerVisibility(CadConstants.CONNECTION_DIST, _config.TextDist.Visible);
            ToggleLayerVisibility("DIM", _config.TextDist.Visible);
            UpdateToggleStyle(btnTglDist, _config.TextDist.Visible);
            AppSettings.Save(_config);
            _doc.Editor.Regen();
        };
        btnTglPt.Click += (s, e) => {
            _config.TextPt.Visible = !_config.TextPt.Visible;
            ToggleLayerVisibility(CadConstants.POINT_NUMBER, _config.TextPt.Visible);
            ToggleLayerVisibility("STNO", _config.TextPt.Visible);
            UpdateToggleStyle(btnTglPt, _config.TextPt.Visible);
            AppSettings.Save(_config);
            _doc.Editor.Regen();
        };
        btnTglComm.Click += (s, e) => {
            _config.TextComm.Visible = !_config.TextComm.Visible;
            ToggleLayerVisibility(CadConstants.SYMB_TEXT, _config.TextComm.Visible);
            ToggleLayerVisibility("CORINF", _config.TextComm.Visible);
            UpdateToggleStyle(btnTglComm, _config.TextComm.Visible);
            AppSettings.Save(_config);
            _doc.Editor.Regen();
        };
 
        spScale.Children.Add(btnTglBrg);
        spScale.Children.Add(btnTglDist);
        spScale.Children.Add(btnTglPt);
        spScale.Children.Add(btnTglComm);
 
        sp.Children.Add(spScale);
 
        btnSound = new Button() { Content = "\ud83d\udd0a", Width = 28, Height = 28, FontSize = 14, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "Toggle Audio Feedback", Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        btnSound.Click += (s, e) => { 
            _config.AudioFeedback = !_config.AudioFeedback; 
            AppSettings.Save(_config);
            UpdateSoundIcon(); 
            if (_config.AudioFeedback) PlayAudio();
        };
 
        Button btnAbout = new Button() { Content = "?", Width = 28, Height = 28, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.LightGray, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "About / Help", Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        btnAbout.Click += (s, e) => ShowAboutPopup();
 
        sp.Children.Add(btnSound);
        sp.Children.Add(btnAbout);
        return sp;
    }  

    private void UpdateToggleStyle(Button btn, bool isVisible)
    {
        btn.Opacity = isVisible ? 1.0 : 0.3;
    }

    // 4. Absolute Scaling & Offset Logic
    private void ScaleAllText()
    {
        if (!ValidateDocument()) return;
        var ed = _doc.Editor;
        int count = 0;

        ExecuteUiAction(() => {
            using (DocumentLock loc = _doc.LockDocument())
            using (Transaction tr = _doc.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                string[] targetLayers = { 
                    CadConstants.BDY_BEARING, CadConstants.BDY_DISTANCE, 
                    CadConstants.CONNECTION_BEAR, CadConstants.CONNECTION_DIST, 
                    "BEAR", "DIM" 
                };

                foreach (ObjectId id in btr)
                {
                    Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    if (ent is DBText dbt)
                    {
                        string layer = dbt.Layer;
                        double basePaperHeight = CadConstants.GetReferenceHeight(layer);
                        bool isTargetLayer = targetLayers.Any(l => string.Equals(l, layer, StringComparison.OrdinalIgnoreCase));

                        // Task 2: Absolute Pro-Rata Scaling Math
                        // We only move text on specific layers with specific justifications
                        if (isTargetLayer && (dbt.Justify == AttachmentPoint.BottomCenter || dbt.Justify == AttachmentPoint.TopCenter))
                        {
                            try
                            {
                                dbt.UpgradeOpen();

                                // Absolute Offset Math: maintain a 1.5mm paper-space gap
                                // currentLabelScale is derived from height and its constant reference
                                double currentLabelScale = (dbt.Height / basePaperHeight) * 1000.0;
                                double moveDist = (_plotScale - currentLabelScale) * (1.5 / 1000.0);

                                if (Math.Abs(moveDist) > 1e-6)
                                {
                                    double dir = 0;
                                    if (dbt.Justify == AttachmentPoint.BottomCenter)
                                    {
                                        dir = dbt.Rotation + (Math.PI / 2.0); // Perpendicular outward
                                    }
                                    else if (dbt.Justify == AttachmentPoint.TopCenter)
                                    {
                                        dir = dbt.Rotation - (Math.PI / 2.0); // Perpendicular outward
                                    }

                                    Vector3d displacement = new Vector3d(Math.Cos(dir) * moveDist, Math.Sin(dir) * moveDist, 0);
                                    dbt.AlignmentPoint += displacement;
                                }

                                dbt.Height = GetModelSize(basePaperHeight);
                                count++;
                            }
                            catch { }
                        }
                        else 
                        {
                            // Task 2: Just update height for other layers (Point Number, etc.)
                            dbt.UpgradeOpen();
                            dbt.Height = GetModelSize(basePaperHeight);
                            count++;
                        }
                    }
                }

                _prevPlotScale = _plotScale;
                tr.Commit();
                ed.WriteMessage($"\n[Scale] Absolute pro-rata sync complete. {count} labels updated for 1:{_plotScale}.");
                ed.UpdateScreen();
                ed.Regen();

                ReturnToBearing();
            }
        });
    }
    private void UpdateSoundIcon()
    {
        if (btnSound == null) return;
        btnSound.Foreground = _config.AudioFeedback ? UITheme.AccentColor : Brushes.Gray;
    }

    private void ShowAboutPopup()
    {
        string aboutMsg = "TECHNICAL HELP MANUAL - CADASTRE LINES\n\n" +
                          "SECTION 1: CORE SYSTEM WORKFLOW\n" +
                          "To initialize a traverse, establish the base coordinate origin by either manual Easting/Northing entry (End key) or by selecting an existing AutoCAD node directly in the drawing space (PgDn key). Once the origin is set, entering consecutive bearings and distances will automatically sequence the line geometry and draft the corresponding surveyor annotations.\n\n" +
                          "SECTION 2: SMART FIELD & INPUT CAPABILITIES\n" +
                          "Both the Bearing and Distance fields feature real-time inline evaluation, allowing you to enter mathematical equations (e.g., combining measurements using +, -, *, or / operators). Additionally, the active drawing layer can be switched instantly using the Q, W, E, A, S, and D hotkeys, which also dynamically updates the control panel UI colors.\n\n" +
                          "SECTION 3: TRAVERSAL NAVIGATION & SHORTCUTS\n" +
                          " • Arrow Keys: Instantly shift the current bearing by 90\u00B0 or 180\u00B0 increments.\n" +
                          " • PgUp: Routes into the independent Side Shot/Radiation geometry window.\n" +
                          " • Ins: Places a distinct, standalone text remark at the current station.\n" +
                          " • Del: Securely steps backward by undoing the last line creation and automatically re-aligning the station point counter.\n\n" +
                          "SECTION 4: INTERACTIVE ANNOTATION UTILITIES\n" +
                          " • Swap Text: Scans the immediate perimeter of a boundary line to invert the relative placement of bearing and distance strings.\n" +
                          " • 180\u00B0 Text: Targets and reverses the angle readout text on structural lines.\n" +
                          " • Annotate Line: Calculates properties on selected CAD vectors to place clean surveyor strings dynamically.\n\n" +
                          "SECTION 5: REGIONAL STANDARD MACRO SWEEPS\n" +
                          " • QLD Format: Translates geometry to statutory layers (70, 35, TRAV, AABT), switches fonts to survacad.shx, applies a cursive 20\u00B0 slant to dimensions, formats measurements using pipe character markers (|), and converts outputs to AutoCAD symbol code syntax (%%d, %%135, %%136).\n" +
                          " • NT Format: Completes a comprehensive cleanup pass to remove redundant trailing zero indicators on survey distances and strips empty minutes or seconds from bearings.";

        HelpWpfWindow hWin = new HelpWpfWindow(aboutMsg);
        hWin.Owner = System.Windows.Window.GetWindow(this);
        hWin.Show(); // Modeless
    }
    #endregion

    #region Tab Building
    private object BuildInputTab()
    {
        Grid mainG = new Grid();
        mainG.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 0: Data Card
        mainG.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 1: Quick Actions Card
        mainG.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 2: Layer Card
        mainG.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 3: Annotation Card
        mainG.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) });

        // --- 1. DATA ENTRY CARD ---
        Border cardData = UITheme.CreateCard(); cardData.Margin = new Thickness(10, 6, 10, 6);
        StackPanel spData = new StackPanel();

        // --- Traverse Setup Row (E & N / PICK) ---
        Grid gPos = new Grid();
        gPos.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
        gPos.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
        gPos.Margin = new Thickness(0, 0, 0, 8);

        Button btnEN = UITheme.CreateActionBtn("", UITheme.ActionBlue);
        btnEN.Content = UITheme.CreateShortcutContent("End", "\ud83d\udccd E & N");
        btnEN.Height = 32; btnEN.Margin = new Thickness(0, 0, 3, 0);
        btnEN.ToolTip = "Enter starting coordinates manually (Easting/Northing).";
        btnEN.Click += (s, e) => TriggerCoordsWindow();

        Button btnPick = UITheme.CreateActionBtn("", UITheme.ActionBlue);
        btnPick.Content = UITheme.CreateShortcutContent("PgDn", "\ud83d\uddb1\ufe0f PICK");
        btnPick.Height = 32; btnPick.Margin = new Thickness(3, 0, 0, 0);
        btnPick.ToolTip = "Select a starting point directly from the AutoCAD drawing screen.";
        btnPick.Click += (s, e) => ExecuteScreenPick();

        Grid.SetColumn(btnEN, 0); Grid.SetColumn(btnPick, 1);
        gPos.Children.Add(btnEN); gPos.Children.Add(btnPick);
        spData.Children.Add(gPos);

        // --- Master Input Grid (Aligns Bearing and Distance Columns) ---
        Grid gInputMaster = new Grid();
        gInputMaster.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
        gInputMaster.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(136) });
        
        gInputMaster.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 0: Bearing Label
        gInputMaster.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 1: Bearing Row
        gInputMaster.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 2: Bearing Trace
        gInputMaster.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 3: Distance Label
        gInputMaster.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 4: Distance Row
        gInputMaster.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 5: Distance Trace

        // Bearing Label
        Label lblBrg = UITheme.CreateLabel("BEARING (DDD.MMSS)");
        Grid.SetRow(lblBrg, 0); Grid.SetColumnSpan(lblBrg, 2);
        gInputMaster.Children.Add(lblBrg);

        // txtBearing (Row 1, Col 0)
        txtBearing = UITheme.CreateInputBox(); 
        txtBearing.Height = 32;
        txtBearing.PreviewKeyDown += Input_PreviewKeyDown;
        Grid.SetRow(txtBearing, 1); Grid.SetColumn(txtBearing, 0);
        gInputMaster.Children.Add(txtBearing);

        // Bearing Buttons (Row 1, Col 1)
        Grid gBrgBtns = new Grid();
        for (int i = 0; i < 4; i++) gBrgBtns.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(34) });

        Button CreateBrgBtn(string text, string tip, double delta) {
            Button b = new Button() { Content = text, Width = 32, Height = 32, Margin = new Thickness(2, 0, 0, 0), Background = UITheme.ActionBlue, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 10, ToolTip = tip };
            b.Click += (s, e) => { ModifyBearing(delta); txtBearing.Focus(); txtBearing.SelectAll(); };
            return b;
        }

        Button bP90 = CreateBrgBtn("+90", "\u21BB Rotate bearing +90\u00B0", 90);
        Button bM90 = CreateBrgBtn("-90", "\u21BA Rotate bearing -90\u00B0", -90);
        Button bP180 = CreateBrgBtn("+180", "\u21C5 Rotate bearing +180\u00B0", 180);
        Button bM180 = CreateBrgBtn("-180", "\u21C5 Rotate bearing -180\u00B0", -180);

        Grid.SetColumn(bP90, 0); Grid.SetColumn(bM90, 1); Grid.SetColumn(bP180, 2); Grid.SetColumn(bM180, 3);
        gBrgBtns.Children.Add(bP90); gBrgBtns.Children.Add(bM90); gBrgBtns.Children.Add(bP180); gBrgBtns.Children.Add(bM180);
        Grid.SetRow(gBrgBtns, 1); Grid.SetColumn(gBrgBtns, 1);
        gInputMaster.Children.Add(gBrgBtns);

        // Bearing Trace (Row 2)
        lblBearingTrace = new TextBlock() { FontSize = 12, Foreground = Brushes.LightGray, FontStyle = FontStyles.Italic, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, -1, 0, 6) };
        Grid.SetRow(lblBearingTrace, 2); Grid.SetColumnSpan(lblBearingTrace, 2);
        gInputMaster.Children.Add(lblBearingTrace);

        // Distance Label (Row 3)
        Label lblDist = UITheme.CreateLabel("DISTANCE (m)");
        Grid.SetRow(lblDist, 3); Grid.SetColumnSpan(lblDist, 2);
        gInputMaster.Children.Add(lblDist);

        // Wrap txtDistance and btnFeet in a sub-grid to support Feet-to-Meters conversions
        Grid gDistSub = new Grid();
        gDistSub.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
        gDistSub.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(42) });

        txtDistance = UITheme.CreateInputBox();
        txtDistance.Height = 32;
        txtDistance.PreviewKeyDown += Input_PreviewKeyDown;
        txtDistance.GotFocus += (s, e) => { txtDistance.BorderBrush = Brushes.WhiteSmoke; txtDistance.BorderThickness = new Thickness(1.5); };
        txtDistance.LostFocus += (s, e) => { txtDistance.BorderBrush = Brushes.Gray; txtDistance.BorderThickness = new Thickness(1); };
        txtDistance.TextChanged += (s, e) => { _distanceIsConverted = false; };
        Grid.SetColumn(txtDistance, 0);
        gDistSub.Children.Add(txtDistance);

        Button btnFeet = new Button() {
            Content = "m",
            Width = 38,
            Height = 32,
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 10.5,
            ToolTip = "Toggle Input Unit: Meters (m) / Feet (ft)"
        };
        
        btnFeet.Click += (s, e) => {
            _feetMode = !_feetMode;
            if (_feetMode)
            {
                btnFeet.Background = new SolidColorBrush(Color.FromRgb(243, 156, 18)); // Vibrant survey orange
                btnFeet.Foreground = Brushes.Black;
                btnFeet.Content = "ft";
                btnFeet.FontWeight = FontWeights.Bold;
                lblDistanceTrace.Text = "[UNIT ACTIVE] Input distances in FEET (will convert to METERS).";
            }
            else
            {
                btnFeet.Background = new SolidColorBrush(Color.FromRgb(60, 60, 60));
                btnFeet.Foreground = Brushes.White;
                btnFeet.Content = "m";
                btnFeet.FontWeight = FontWeights.SemiBold;
                lblDistanceTrace.Text = "[UNIT ACTIVE] Input distances in METERS.";
            }
            txtDistance.Focus();
            txtDistance.SelectAll();
        };

        Grid.SetColumn(btnFeet, 1);
        gDistSub.Children.Add(btnFeet);

        Grid.SetRow(gDistSub, 4); Grid.SetColumn(gDistSub, 0);
        gInputMaster.Children.Add(gDistSub);

        // SIDE SHOT Button (Row 4, Col 1)
        Button bSS = new Button() { 
            Content = UITheme.CreateShortcutContent("PgUp", "\u2699 SIDE SHOT"),
            Height = 32, 
            Width = 134,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0), 
            Background = UITheme.ActionBlue, 
            Foreground = Brushes.White, 
            FontWeight = FontWeights.Bold, 
            FontSize = 10,
            ToolTip = "Open Side Shot/Radiation menu (PGUP)" 
        };
        bSS.Click += (s, e) => { OpenSideShotForm(); txtBearing.Focus(); txtBearing.SelectAll(); };
        Grid.SetRow(bSS, 4); Grid.SetColumn(bSS, 1);
        gInputMaster.Children.Add(bSS);

        // Distance Trace (Row 5)
        lblDistanceTrace = new TextBlock() { FontSize = 12, Foreground = Brushes.LightGray, FontStyle = FontStyles.Italic, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 1, 0, 6) };
        Grid.SetRow(lblDistanceTrace, 5); Grid.SetColumnSpan(lblDistanceTrace, 2);
        gInputMaster.Children.Add(lblDistanceTrace);

        spData.Children.Add(gInputMaster);
        cardData.Child = spData;
        Grid.SetRow(cardData, 0); mainG.Children.Add(cardData);

        // --- 2. QUICK ACTIONS CARD ---
        Border cardQuick = UITheme.CreateCard(); cardQuick.Margin = new Thickness(10, 0, 10, 6);
        Grid gActions = new Grid();
        for (int i = 0; i < 2; i++) gActions.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });

        Button CreateQuickBtn(object content, string tip, Action action)
        {
            Button b = new Button() { Content = content, Height = 32, Margin = new Thickness(1.5), Background = UITheme.ActionBlue, Foreground = Brushes.White, FontWeight = FontWeights.Bold, ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (s, e) => { action(); txtBearing.Focus(); txtBearing.SelectAll(); };
            return b;
        }

        Button bUndo = CreateQuickBtn(UITheme.CreateShortcutContent("Del", "\u21B2 UNDO"), "Delete last line/text (DEL)", () => ExecuteUiAction(() => UndoLastStep()));
        Button bComm = CreateQuickBtn(UITheme.CreateShortcutContent("Ins", "\ud83d\udcac COMMENT"), "Add Text Comment/Symbol (INS)", () => ExecuteUiAction(() => AddTextComment(null)));

        Grid.SetColumn(bUndo, 0); Grid.SetColumn(bComm, 1);
        gActions.Children.Add(bUndo); gActions.Children.Add(bComm);

        cardQuick.Child = gActions;
        Grid.SetRow(cardQuick, 1); mainG.Children.Add(cardQuick);

        // --- 3. LAYER SELECTION CARD ---
        Border cardLay = UITheme.CreateCard(); 
        cardLay.Margin = new Thickness(10, 0, 10, 10);
        cardLay.Padding = new Thickness(4);
        StackPanel spLay = new StackPanel();
        spLay.Children.Add(UITheme.CreateLabel("ACTIVE LAYER (QWE ASD)"));
        Grid g = new Grid() { Margin = new Thickness(0) };
        g.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
        for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });

        btnQ = UITheme.CreateLayerBtn("Q"); btnQ.HorizontalAlignment = HorizontalAlignment.Stretch; btnQ.Height = 36; btnQ.Click += (s, e) => SetCurrentLayer(LayerConfig[Key.Q].Name, btnQ);
        btnW = UITheme.CreateLayerBtn("W"); btnW.HorizontalAlignment = HorizontalAlignment.Stretch; btnW.Height = 36; btnW.Click += (s, e) => SetCurrentLayer(LayerConfig[Key.W].Name, btnW);
        btnE = UITheme.CreateLayerBtn("E"); btnE.HorizontalAlignment = HorizontalAlignment.Stretch; btnE.Height = 36; btnE.Click += (s, e) => SetCurrentLayer(LayerConfig[Key.E].Name, btnE);
        btnA = UITheme.CreateLayerBtn("A"); btnA.HorizontalAlignment = HorizontalAlignment.Stretch; btnA.Height = 36; btnA.Click += (s, e) => SetCurrentLayer(LayerConfig[Key.A].Name, btnA);
        btnS = UITheme.CreateLayerBtn("S"); btnS.HorizontalAlignment = HorizontalAlignment.Stretch; btnS.Height = 36; btnS.Click += (s, e) => SetCurrentLayer(LayerConfig[Key.S].Name, btnS);
        btnD = UITheme.CreateLayerBtn("D"); btnD.HorizontalAlignment = HorizontalAlignment.Stretch; btnD.Height = 36; btnD.Click += (s, e) => SetCurrentLayer(LayerConfig[Key.D].Name, btnD);

        Grid.SetRow(btnQ, 0); Grid.SetColumn(btnQ, 0); Grid.SetRow(btnW, 0); Grid.SetColumn(btnW, 1); Grid.SetRow(btnE, 0); Grid.SetColumn(btnE, 2);
        Grid.SetRow(btnA, 1); Grid.SetColumn(btnA, 0); Grid.SetRow(btnS, 1); Grid.SetColumn(btnS, 1); Grid.SetRow(btnD, 1); Grid.SetColumn(btnD, 2);

        g.Children.Add(btnQ); g.Children.Add(btnW); g.Children.Add(btnE);
        g.Children.Add(btnA); g.Children.Add(btnS); g.Children.Add(btnD);
        spLay.Children.Add(g); cardLay.Child = spLay;
        Grid.SetRow(cardLay, 2); mainG.Children.Add(cardLay);

        // --- 4. ANNOTATION TOOLS CARD ---
        Border cardAnn = UITheme.CreateCard();
        cardAnn.Margin = new Thickness(10, 0, 10, 6);
        StackPanel spAnn = new StackPanel();
        spAnn.Children.Add(UITheme.CreateLabel("ANNOTATION TOOLS"));

        Grid gAnn = new Grid();
        gAnn.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
        gAnn.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });

        Grid r1 = new Grid();
        r1.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
        r1.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
        r1.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });

        Button btnSwapText = UITheme.CreateActionBtn("Swap Text", UITheme.ActionBlue); btnSwapText.Height = 28; btnSwapText.Margin = new Thickness(1.5);
        btnSwapText.FontSize = 10;
        btnSwapText.ToolTip = "Swaps the positions of bearing and distance labels.";
        btnSwapText.Click += (s, e) => ExecuteUiAction(() => ExecuteSwapText());
        Grid.SetColumn(btnSwapText, 0); r1.Children.Add(btnSwapText);

        Button btnRot180 = UITheme.CreateActionBtn("180\u00B0 Text", UITheme.ActionBlue); btnRot180.Height = 28; btnRot180.Margin = new Thickness(1.5);
        btnRot180.FontSize = 10;
        btnRot180.ToolTip = "Reverses the direction of a selected bearing by adding 180 degrees.";
        btnRot180.Click += (s, e) => ExecuteUiAction(() => RotateBearingText());
        Grid.SetColumn(btnRot180, 1); r1.Children.Add(btnRot180);

        Button btnAnnotate = UITheme.CreateActionBtn("Annotate Line", UITheme.ActionBlue); btnAnnotate.Height = 28; btnAnnotate.Margin = new Thickness(1.5);
        btnAnnotate.FontSize = 10;
        btnAnnotate.ToolTip = "Pick a drawing line to automatically calculate and place new labels on it.";
        btnAnnotate.Click += (s, e) => ExecuteUiAction(() => AnnotateSelectedLine());
        Grid.SetColumn(btnAnnotate, 2); r1.Children.Add(btnAnnotate);

        Grid.SetRow(r1, 0); gAnn.Children.Add(r1);

        Grid r2 = new Grid();
        r2.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
        r2.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });

        Button btnQld = UITheme.CreateActionBtn("QLD Format", UITheme.ActionBlue); btnQld.Height = 28; btnQld.Margin = new Thickness(1.5);
        btnQld.FontSize = 10;
        btnQld.ToolTip = "Performs a full drawing sweep to convert cadastre layers, apply 20-degree obliquing to dimensions, change fonts to survacad.shx, and insert specialized surveyor symbol codes.";
        btnQld.Click += (s, e) => ExecuteUiAction(() => ApplyQLDStandards());
        Grid.SetColumn(btnQld, 0); r2.Children.Add(btnQld);

        Button btnNt = UITheme.CreateActionBtn("NT Format", UITheme.ActionBlue); btnNt.Height = 28; btnNt.Margin = new Thickness(1.5);
        btnNt.FontSize = 10;
        btnNt.ToolTip = "Performs a drawing sweep to clean up text strings by truncating trailing decimal zeros on distances and removing zero minutes or seconds on bearings.";
        btnNt.Click += (s, e) => ExecuteUiAction(() => ApplyNTStandards());
        Grid.SetColumn(btnNt, 1); r2.Children.Add(btnNt);

        Grid.SetRow(r2, 1); gAnn.Children.Add(r2);

        spAnn.Children.Add(gAnn);
        cardAnn.Child = spAnn;
        Grid.SetRow(cardAnn, 3); mainG.Children.Add(cardAnn);

        // --- 5. TRAVERSE HISTORY CARD ---
        Border cardHistory = UITheme.CreateCard();
        cardHistory.Margin = new Thickness(10, 0, 10, 10);
        
        Grid gHistoryLayout = new Grid();
        gHistoryLayout.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // Title & Stats
        gHistoryLayout.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) }); // Scrollable History Panel
        
        // Title & Stats
        StackPanel spHeader = new StackPanel();
        spHeader.Children.Add(UITheme.CreateLabel("TRAVERSE SEGMENT HISTORY"));
        
        lblStats = new TextBlock() {
            Text = "No segments recorded yet.",
            Foreground = Brushes.LightGray,
            FontSize = 10.5,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 0, 0, 5)
        };
        spHeader.Children.Add(lblStats);
        Grid.SetRow(spHeader, 0);
        gHistoryLayout.Children.Add(spHeader);
        
        // Scrollable History
        svHistory = new ScrollViewer() { 
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, 
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 300,
            MinHeight = 100
        };
        pnlHistory = new StackPanel();
        svHistory.Content = pnlHistory;
        Grid.SetRow(svHistory, 1);
        gHistoryLayout.Children.Add(svHistory);
        
        cardHistory.Child = gHistoryLayout;
        Grid.SetRow(cardHistory, 4); mainG.Children.Add(cardHistory);

        return mainG;
    }
    #endregion

    #region Calculation & Analysis
    private void CalculateArea()
    {
    }

    private void UpdateRunningMisclosure()
    {
    }
    #endregion

    #region UI & Input Handlers
    private void ReturnToBearing()
    {
        txtBearing.Focus();
        txtBearing.SelectAll();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (LayerConfig.ContainsKey(e.Key))
        {
            var def = LayerConfig[e.Key];
            Button b = e.Key switch { Key.Q => btnQ, Key.W => btnW, Key.E => btnE, Key.A => btnA, Key.S => btnS, Key.D => btnD, _ => btnW };
            SetCurrentLayer(def.Name, b);
            e.Handled = true;
        }

        if (e.Key == Key.PageUp) { e.Handled = true; OpenSideShotForm(); ReturnToBearing(); }
        else if (e.Key == Key.PageDown) { e.Handled = true; ExecuteScreenPick(); ReturnToBearing(); }
        else if (e.Key == Key.End) { e.Handled = true; TriggerCoordsWindow(); ReturnToBearing(); }
        else if (e.Key == Key.Insert) { e.Handled = true; ExecuteUiAction(() => AddTextComment(null)); ReturnToBearing(); }
        else if (e.Key == Key.Delete) { e.Handled = true; ExecuteUiAction(() => UndoLastStep()); ReturnToBearing(); }
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        TextBox tb = (TextBox)sender;
        if (tb == txtBearing)
        {
            if (e.Key == Key.Up) { ModifyBearing(90); e.Handled = true; }
            if (e.Key == Key.Down) { ModifyBearing(-90); e.Handled = true; }
            if (e.Key == Key.Right) { ModifyBearing(180); e.Handled = true; }
            if (e.Key == Key.Left) { ModifyBearing(-180); e.Handled = true; }
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string input = tb.Text.Trim();
            
            if (input.Contains("+") || input.Contains("-") || input.Contains("*") || input.Contains("/"))
            {
                string oldVal = input;
                string result = EvaluateInlineExpression(input, tb == txtBearing);
                if (result != null)
                {
                    tb.Text = result;
                    if (tb == txtBearing) lblBearingTrace.Text = $"{oldVal} = {result} ({CadMath.FormatAsSurveyor(double.Parse(result))})";
                    else
                    {
                        if (_feetMode && !_distanceIsConverted && double.TryParse(result, out double ftVal))
                        {
                            double mVal = ftVal * 0.3048;
                            tb.Text = mVal.ToString("0.000");
                            _distanceIsConverted = true;
                            lblDistanceTrace.Text = $"{oldVal} = {ftVal:F3} ft = {mVal:F3} m";
                        }
                        else
                        {
                            lblDistanceTrace.Text = $"{oldVal} = {result}";
                        }
                    }
                    
                    tb.Foreground = Brushes.White;
                    tb.FontWeight = FontWeights.Bold;
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    timer.Tick += (s, ev) => { tb.Foreground = Brushes.Cyan; tb.FontWeight = FontWeights.Normal; timer.Stop(); };
                    timer.Start();

                    tb.SelectAll();
                    return; // Wait for next Enter to move focus or draw
                }
            }

            if (string.IsNullOrWhiteSpace(txtBearing.Text) && string.IsNullOrWhiteSpace(txtDistance.Text))
            {
                if (!_hasStartPoint)
                {
                    TriggerCoordsWindow();
                    return;
                }
                else
                {
                    txtBearing.Focus();
                }
            }
            else
            {
                if (tb == txtBearing)
                {
                    if (!CadMath.TryParseBearing(tb.Text, out double _))
                    {
                        lblBearingTrace.Text = "";
                        tb.Focus();
                        tb.SelectAll();
                        return;
                    }
                    txtDistance.Focus();
                    txtDistance.SelectAll();
                }                else if (tb == txtDistance) 
                {
                    if (string.IsNullOrWhiteSpace(txtBearing.Text) || string.IsNullOrWhiteSpace(txtDistance.Text))
                    {
                        // Don't draw if one is missing, but also don't show warning if just tabbing through
                        if (string.IsNullOrWhiteSpace(txtBearing.Text)) txtBearing.Focus();
                        return;
                    }

                    if (_feetMode && !_distanceIsConverted && double.TryParse(tb.Text, out double ftVal))
                    {
                        double mVal = ftVal * 0.3048;
                        tb.Text = mVal.ToString("0.000");
                        _distanceIsConverted = true;
                        lblDistanceTrace.Text = $"{ftVal:F3} ft = {mVal:F3} m";
                    }

                    if (!_hasStartPoint)
                    {
                        TriggerCoordsWindow();
                        // If start point was established, proceed to draw
                        if (_hasStartPoint) ExecuteUiAction(() => ExecuteManualDraw());
                    }
                    else
                    {
                        ExecuteUiAction(() => ExecuteManualDraw());
                    }
                }
            }
        }
    }

    internal string? EvaluateInlineExpression(string input, bool isDms)
    {
        try
        {
            if (isDms)
            {
                string[] parts;
                if (input.Contains("+"))
                {
                    parts = input.Split('+');
                    double sum = double.Parse(parts[0].Trim());
                    for (int i = 1; i < parts.Length; i++)
                    {
                        sum = CadMath.AddSubDms(sum, double.Parse(parts[i].Trim()), true);
                    }
                    return CadMath.DmsToString(sum);
                }
                else if (input.Contains("-"))
                {
                    parts = input.Split('-');
                    double diff = double.Parse(parts[0].Trim());
                    for (int i = 1; i < parts.Length; i++)
                    {
                        diff = CadMath.AddSubDms(diff, double.Parse(parts[i].Trim()), false);
                    }
                    return CadMath.DmsToString(diff);
                }
            }
            
            System.Data.DataTable dt = new System.Data.DataTable();
            var v = dt.Compute(input, "");
            double res = Convert.ToDouble(v);
            return isDms ? CadMath.DmsToString(res) : res.ToString("0.000");
        }
        catch
        {
            _doc.Editor.WriteMessage("\n[Error] Invalid Math Expression.");
            return null;
        }
    }
    #endregion

    #region Primary Drawing Logic
    private void ExecuteManualDraw()
    {
        if (!_hasStartPoint)
        {
            _doc.Editor.WriteMessage("\n[Error] No start point defined. Please set a point first.");
            return;
        }
        if (!EnsureQuiescent()) return;
        if (!ValidateDocument()) return;

        using (DocumentLock loc = _doc.LockDocument())
        using (Transaction tr = _doc.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            Point3d newPt = DrawGeometryToDatabase(tr, btr, txtBearing.Text, txtDistance.Text, _currentPoint, _currentLayer);
            
            int currentNum = DwgDataManager.GetNextPointNumber(tr, _doc.Database) - 1;
            tr.Commit();

            _lastCreatedVertex = newPt; _currentPoint = newPt; _traversePath.Add(newPt);

            CalculateArea(); PlayAudio(); PanToPoint(newPt); _doc.Editor.UpdateScreen();
            
            this.Dispatcher.BeginInvoke(new Action(() => {
                txtBearing.Focus();
                txtBearing.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void SetStartPoint(Point3d pt)
    {
        if (!EnsureQuiescent()) return;
        if (!ValidateDocument()) return;
        
        ExecuteUiAction(() => {
            _currentPoint = pt; _lastCreatedVertex = pt; _hasStartPoint = true;
            _undoStack.Clear(); _traversePath.Clear(); _traversePath.Add(_currentPoint);
            _segmentHistory.Clear();
            UpdateHistoryUi();

            using (DocumentLock loc = _doc.LockDocument())
            using (Transaction tr = _doc.TransactionManager.StartTransaction())
            {
                bool exists = DwgDataManager.IsPointNumberAtLocation(pt, tr, _doc.Database);
                if (!exists)
                {
                    int nextNum = DwgDataManager.GetNextPointNumber(tr, _doc.Database);
                    BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    TextSettings ptSettings = new TextSettings { Size = 2.5, Style = "ROMAND140" };
                    Entity ptTxt = CreateText(nextNum.ToString(), CadConstants.POINT_NUMBER, pt, AttachmentPoint.BottomLeft, tr, _doc.Database, ptSettings);
                    AddToDb(ptTxt, btr, tr);
                    DwgDataManager.SetNextPointNumber(nextNum + 1, tr, _doc.Database);
                }
                tr.Commit();
            }

            CalculateArea();
            lblBearingTrace.Text = ""; lblDistanceTrace.Text = "";
            txtBearing.Focus();
            txtBearing.SelectAll();
            PanToPoint(pt);
            _doc.Editor.WriteMessage($"\n[Ready] Start point set at {_currentPoint.X:0.000}, {_currentPoint.Y:0.000}");
        });
    }

    private Point3d DrawGeometryToDatabase(Transaction tr, BlockTableRecord btr, string brgStr, string distStr, Point3d startPt, string layer)
    {
        double rawBrg, dist;
        if (!CadMath.TryParseBearing(brgStr, out rawBrg) || !double.TryParse(distStr, out dist))
        {
            _doc.Editor.WriteMessage("\n[Error] Invalid Bearing or Distance format.");
            throw new System.Exception("Invalid Bearing or Distance format.");
        }
        
        // Ensure layer exists before database operation
        bool layerExists = false;
        using (Transaction checkTr = btr.Database.TransactionManager.StartTransaction())
        {
            LayerTable lt = (LayerTable)checkTr.GetObject(btr.Database.LayerTableId, OpenMode.ForRead);
            layerExists = lt.Has(layer);
            checkTr.Commit();
        }
        if (!layerExists) EnsureLayerExists(layer);

        double angleDeg = CadMath.ParseDmsToDegrees(rawBrg);
        double cadAngleRad = (90.0 - angleDeg) * (Math.PI / 180.0);
        Point3d endPoint = new Point3d(startPt.X + (dist * Math.Cos(cadAngleRad)), startPt.Y + (dist * Math.Sin(cadAngleRad)), startPt.Z);
        endPoint = CheckSnapping(endPoint, tr, btr);
        List<ObjectId> createdEntities = new List<ObjectId>();
        Autodesk.AutoCAD.DatabaseServices.Line ln = new Autodesk.AutoCAD.DatabaseServices.Line(startPt, endPoint);
        ln.Layer = layer;

        // Apply linetype scale from hardcoded config
        var configEntry = LayerConfig.Values.FirstOrDefault(ld => ld.Name == layer);
        if (!string.IsNullOrEmpty(configEntry.Name))
        {
            ln.LinetypeScale = configEntry.LinetypeScale;
        }

        createdEntities.Add(AddToDb(ln, btr, tr));
        createdEntities.AddRange(CreateAnnotatedText(btr, tr, ln, rawBrg, dist, cadAngleRad));

        // FIX: Check if number exists before creating
        bool exists = DwgDataManager.IsPointNumberAtLocation(endPoint, tr, btr.Database);
        if (!exists)
        {
            int nextNum = DwgDataManager.GetNextPointNumber(tr, btr.Database);
            TextSettings ptSettings = new TextSettings { Size = 2.5, Style = "ROMAND140" };
            createdEntities.Add(AddToDb(CreateText(nextNum.ToString(), CadConstants.POINT_NUMBER, endPoint, AttachmentPoint.BottomLeft, tr, btr.Database, ptSettings), btr, tr));
            DwgDataManager.SetNextPointNumber(nextNum + 1, tr, btr.Database);
        }

        _undoStack.Push(createdEntities);
        
        _segmentHistory.Add(new TraverseSegment {
            Index = _segmentHistory.Count + 1,
            Bearing = CadMath.FormatAsSurveyor(rawBrg),
            Distance = dist.ToString("0.000"),
            Layer = layer,
            EndPoint = endPoint
        });
        UpdateHistoryUi();
 
        return endPoint;
    }
    #endregion

    #region Database Operations
    private void UndoLastStep()
    {
        if (_undoStack.Count == 0) return;
        if (!ValidateDocument()) return;

        using (DocumentLock loc = _doc.LockDocument())
        using (Transaction tr = _doc.TransactionManager.StartTransaction())
        {
            List<ObjectId> stepObjects = _undoStack.Pop();
            bool pointDeleted = false;
            foreach (ObjectId id in stepObjects)
            {
                if (!id.IsErased)
                {
                    Entity ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                    if (ent.Layer == CadConstants.POINT_NUMBER) pointDeleted = true;
                    if (ent is Autodesk.AutoCAD.DatabaseServices.Line ln) { _currentPoint = ln.StartPoint; _lastCreatedVertex = ln.StartPoint; }
                    ent.Erase();
                }
            }

            if (pointDeleted)
            {
                int current = DwgDataManager.GetNextPointNumber(tr, _doc.Database);
                if (current > 1) DwgDataManager.SetNextPointNumber(current - 1, tr, _doc.Database);
            }

            if (_traversePath.Count > 1) _traversePath.RemoveAt(_traversePath.Count - 1);
            CalculateArea(); tr.Commit(); _doc.Editor.UpdateScreen();
 
            if (_segmentHistory.Count > 0)
            {
                _segmentHistory.RemoveAt(_segmentHistory.Count - 1);
                UpdateHistoryUi();
            }
        }
    }

    private void ToggleLayerVisibility(string layerName, bool isVisible)
    {
        if (!ValidateDocument()) return;

        using (DocumentLock loc = _doc.LockDocument())
        using (Transaction tr = _doc.TransactionManager.StartTransaction())
        {
            LayerTable lt = (LayerTable)tr.GetObject(_doc.Database.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName))
            {
                LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(lt[layerName], OpenMode.ForWrite);
                ltr.IsOff = !isVisible;
            }
            else if (isVisible)
            {
                EnsureLayerExistsInternal(layerName, null, tr, _doc.Database);
            }
            tr.Commit();
        }
    }

    private void EnsureLayerExists(string layerName)
    {
        if (!ValidateDocument()) return;
        
        Autodesk.AutoCAD.Colors.Color? selectedColor = null;
        using (var checkTr = _doc.Database.TransactionManager.StartTransaction())
        {
            LayerTable lt = (LayerTable)checkTr.GetObject(_doc.Database.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                Autodesk.AutoCAD.Windows.ColorDialog cd = new Autodesk.AutoCAD.Windows.ColorDialog();
                if (cd.ShowDialog() == System.Windows.Forms.DialogResult.OK) selectedColor = cd.Color;
                else selectedColor = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
            }
            checkTr.Commit();
        }

        if (selectedColor != null)
        {
            using (_doc.LockDocument())
            using (Transaction tr = _doc.TransactionManager.StartTransaction())
            {
                EnsureLayerExistsInternal(layerName, selectedColor, tr, _doc.Database);
                tr.Commit();
            }
        }
    }

    private void EnsureLayerExistsInternal(string layerName, Autodesk.AutoCAD.Colors.Color? color, Transaction tr, Database db)
    {
        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!lt.Has(layerName))
        {
            lt.UpgradeOpen(); LayerTableRecord ltr = new LayerTableRecord(); ltr.Name = layerName;
            ltr.Color = color ?? AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
            lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true);
        }
    }

    private ObjectId AddToDb(Entity ent, BlockTableRecord btr, Transaction tr)
    {
        ObjectId id = btr.AppendEntity(ent); tr.AddNewlyCreatedDBObject(ent, true); return id;
    }

    private Point3d CheckSnapping(Point3d target, Transaction tr, BlockTableRecord btr)
    {
        foreach (ObjectId id in btr)
        {
            if (id.IsValid)
            {
                Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                if (ent is DBPoint pt && pt.Position.DistanceTo(target) < _config.SnapTolerance) return pt.Position;
            }
        }
        return target;
    }

    private List<ObjectId> CreateAnnotatedText(BlockTableRecord btr, Transaction tr, Entity baseEnt, double rawBrg, double dist, double cadAngleRad)
    {
        List<ObjectId> ids = new List<ObjectId>();
        double textRot = cadAngleRad; 
        double normAng = cadAngleRad % (Math.PI * 2); 
        if (normAng < 0) normAng += (Math.PI * 2);
        bool isFlipped = false; 
        if (normAng > (Math.PI / 2) && normAng <= (3 * Math.PI / 2)) { textRot += Math.PI; isFlipped = true; }
        
        Point3d mid = ((Autodesk.AutoCAD.DatabaseServices.Line)baseEnt).StartPoint + (((Autodesk.AutoCAD.DatabaseServices.Line)baseEnt).EndPoint - ((Autodesk.AutoCAD.DatabaseServices.Line)baseEnt).StartPoint) / 2.0;
        double dx = Math.Cos(cadAngleRad); 
        double dy = Math.Sin(cadAngleRad); 
        
        string brgLayer, distLayer;
        string brgStyle = "STENDOT100";
        string distStyle = "STENDOT100S";

        // QLD Standard Check
        string[] qldLayers = { "70", "35", "TRAV", "AABT" };
        bool isQld = qldLayers.Contains(_currentLayer);

        if (isQld)
        {
            brgLayer = "BEAR";
            distLayer = "DIM";
            brgStyle = "SU";
            distStyle = "SS";
        }
        else if (_currentLayer == "BOUNDARY_SUBJECT")
        {
            brgLayer = CadConstants.BDY_BEARING;
            distLayer = CadConstants.BDY_DISTANCE;
        }
        else
        {
            brgLayer = CadConstants.CONNECTION_BEAR;
            distLayer = CadConstants.CONNECTION_DIST;
            brgStyle = "STENDOT80";
            distStyle = "STENDOT80";
        }

        double offsetDist = GetModelSize(1.5);
        Vector3d upVec = isFlipped ? new Vector3d(dy, -dx, 0) : new Vector3d(-dy, dx, 0);

        string brgText = CadMath.FormatAsSurveyor(rawBrg);
        string distText = dist.ToString("0.000");

        if (isQld)
        {
            brgText = FormatBearingNT(brgText);
            // Replace symbols for QLD standard
            brgText = brgText.Replace("°", "%%d").Replace("'", "%%135").Replace("\"", "%%136");
            distText = FormatDistanceQLD(distText);
        }

        ids.Add(AddToDb(CreateText(brgText, brgLayer, mid + (upVec * offsetDist), AttachmentPoint.BottomCenter, tr, btr.Database, new TextSettings { Style = brgStyle }, textRot), btr, tr));
        ids.Add(AddToDb(CreateText(distText, distLayer, mid - (upVec * offsetDist), AttachmentPoint.TopCenter, tr, btr.Database, new TextSettings { Style = distStyle }, textRot), btr, tr));
        
        return ids;
    }

    private Entity CreateText(string content, string layer, Point3d pt, AttachmentPoint align, Transaction tr, Database db, TextSettings ts, double rotation = 0)
    {
        EnsureLayerExistsInternal(layer, null, tr, db);
        ObjectId styleId = GetTextStyleId(tr, ts.Style, db);
        
        // Task 1: Retrieve base value based on entity's layer name
        double baseHeight = CadConstants.GetReferenceHeight(layer);
        double finalHeight = GetModelSize(baseHeight);

        if (ts.IsMText)
        {
            MText mt = new MText(); 
            mt.Contents = content; 
            mt.Layer = layer; 
            mt.TextHeight = finalHeight; 
            mt.TextStyleId = styleId;
            mt.Rotation = rotation; 
            mt.Location = pt; 
            mt.Attachment = align;
            mt.ColorIndex = 256; // Forced ByLayer
            if (ts.Masking) { mt.BackgroundFill = true; mt.UseBackgroundColor = true; mt.BackgroundScaleFactor = 1.1; }
            return mt;
        }
        else
        {
            DBText dt = new DBText(); 
            dt.TextString = content; 
            dt.Layer = layer; 
            dt.Height = finalHeight; 
            dt.TextStyleId = styleId;
            dt.Rotation = rotation; 
            dt.Position = pt; 
            dt.Justify = align;
            dt.AlignmentPoint = pt;
            dt.ColorIndex = 256; // Forced ByLayer
            if (ts.Style == "STENDOT100S") dt.Oblique = 23.0 * (Math.PI / 180.0);
            if (string.Equals(layer, "DIM", StringComparison.OrdinalIgnoreCase)) dt.Oblique = 20.0 * (Math.PI / 180.0);
            return dt;
        }
    }

    private ObjectId GetTextStyleId(Transaction tr, string styleName, Database db)
    {
        TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        
        TextStyleTableRecord tstr;
        if (tst.Has(styleName)) 
        {
            tstr = (TextStyleTableRecord)tr.GetObject(tst[styleName], OpenMode.ForWrite);
        }
        else
        {
            tst.UpgradeOpen();
            tstr = new TextStyleTableRecord();
            tstr.Name = styleName;

            // Set default font ONLY for new styles
            string fontFile = "romans.shx"; 
            if (styleName.Contains("ROMAND")) fontFile = "romand.shx";
            else if (styleName.Contains("STENDOT")) fontFile = "stendot.shx";
            tstr.FileName = fontFile;

            tst.Add(tstr);
            tr.AddNewlyCreatedDBObject(tstr, true);
        }

        // Enforce properties (but NOT the font name if it already exists)
        if (styleName == "ROMANS80") { tstr.XScale = 0.8; }
        else if (styleName == "ROMAND140") { tstr.XScale = 1.4; }
        else if (styleName == "STENDOT100") { tstr.XScale = 1.0; }
        else if (styleName == "STENDOT100S") { tstr.XScale = 1.0; tstr.ObliquingAngle = 23.0 * (Math.PI / 180.0); }
        else if (styleName == "STENDOT80") { tstr.XScale = 0.8; }

        return tstr.ObjectId;
    }

    private ObjectId EnsureTextStyle(Transaction tr, string styleName, Database db)
    {
        TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (tst.Has(styleName)) return tst[styleName];

        tst.UpgradeOpen();
        TextStyleTableRecord tstr = new TextStyleTableRecord();
        tstr.Name = styleName;
        tstr.FileName = (styleName == "SS" || styleName == "SU") ? "survacad.shx" : "romans.shx";
        tst.Add(tstr);
        tr.AddNewlyCreatedDBObject(tstr, true);
        return tstr.ObjectId;
    }
    #endregion

    #region Helper UI Methods
    private void PlayAudio()
    {
        if (!_config.AudioFeedback) return;

        try
        {
            string soundFile = "";
            string winMedia = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

            switch (_config.AudioSound)
            {
                case "Beep": soundFile = System.IO.Path.Combine(winMedia, "Windows Default.wav"); break;
                case "Asterisk": soundFile = System.IO.Path.Combine(winMedia, "Windows Background.wav"); break; // "Ding"
                case "Exclamation": soundFile = System.IO.Path.Combine(winMedia, "Windows Exclamation.wav"); break;
                case "Hand": soundFile = System.IO.Path.Combine(winMedia, "Windows Critical Stop.wav"); break;
                case "Question": soundFile = System.IO.Path.Combine(winMedia, "Windows Notify System Generic.wav"); break;
                default: soundFile = System.IO.Path.Combine(winMedia, "Windows Background.wav"); break;
            }

            if (File.Exists(soundFile))
            {
                SoundPlayer sp = new SoundPlayer(soundFile);
                sp.Play();
            }
            else
            {
                switch (_config.AudioSound)
                {
                    case "Beep": SystemSounds.Beep.Play(); break;
                    case "Asterisk": SystemSounds.Asterisk.Play(); break;
                    case "Exclamation": SystemSounds.Exclamation.Play(); break;
                    case "Hand": SystemSounds.Hand.Play(); break;
                    case "Question": SystemSounds.Question.Play(); break;
                    default: SystemSounds.Asterisk.Play(); break;
                }
            }
        }
        catch
        {
            System.Console.Beep();
        }
    }

    private void UpdateLayerButtons()
    {
        if (_doc == null || _doc.IsDisposed) return;
        void UpdateBtn(Button b, string layerName, string key)
        {
            if (b == null) return;
            
            StackPanel sp = new StackPanel() { VerticalAlignment = VerticalAlignment.Center };
            TextBlock tbKey = new TextBlock() { 
                Text = key, 
                FontSize = 8.0, 
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                HorizontalAlignment = HorizontalAlignment.Center 
            };
            
            string cleanName = layerName.Replace("BOUNDARY_", "").Replace("CONNECTION_", "");
            if (cleanName.Length > 10) cleanName = cleanName.Substring(0, 10);
            
            TextBlock tbDesc = new TextBlock() { 
                Text = cleanName, 
                FontSize = 10.0, 
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center 
            };
            
            sp.Children.Add(tbKey);
            sp.Children.Add(tbDesc);
            b.Content = sp;
            
            b.Background = new SolidColorBrush(Color.FromRgb(60, 60, 60));
            if (layerName == "NOT SET") return;
            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.TransactionManager.StartTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(_doc.Database.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layerName))
                    {
                        LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(lt[layerName], OpenMode.ForRead);
                        System.Drawing.Color sysCol = ltr.Color.ColorValue;
                        b.Background = new SolidColorBrush(Color.FromRgb(sysCol.R, sysCol.G, sysCol.B));
                        double brt = (sysCol.R * 0.299 + sysCol.G * 0.587 + sysCol.B * 0.114);
                        
                        tbDesc.Foreground = (brt > 128) ? Brushes.Black : Brushes.White;
                        tbKey.Foreground = (brt > 128) ? new SolidColorBrush(Color.FromRgb(60, 60, 60)) : new SolidColorBrush(Color.FromRgb(220, 220, 220));
                    }
                }
            }
            catch { }
        }
        UpdateBtn(btnQ, LayerConfig[Key.Q].Name, "Q"); UpdateBtn(btnW, LayerConfig[Key.W].Name, "W"); UpdateBtn(btnE, LayerConfig[Key.E].Name, "E");
        UpdateBtn(btnA, LayerConfig[Key.A].Name, "A"); UpdateBtn(btnS, LayerConfig[Key.S].Name, "S"); UpdateBtn(btnD, LayerConfig[Key.D].Name, "D");
    }

    private void SetCurrentLayer(string layerName, Button btn)
    {
        _currentLayer = layerName; HighlightActiveLayer(btn); txtBearing.Focus();
    }

    private void HighlightActiveLayer(Button active)
    {
        Button[] btns = { btnQ, btnW, btnE, btnA, btnS, btnD };
        foreach (var b in btns) { b.BorderThickness = new Thickness(1); b.BorderBrush = Brushes.Gray; }
        active.BorderThickness = new Thickness(3); active.BorderBrush = Brushes.White;
    }

    private bool ValidateDocument()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null || doc != _doc)
        {
            MessageBox.Show("Active document changed or lost. Please restart the tool in the correct document.");
            return false;
        }
        return true;
    }

    private void ModifyBearing(double deltaDegrees)
    {
        double currentVal = 0;
        if (CadMath.TryParseBearing(txtBearing.Text, out currentVal))
        {
            double decDeg = CadMath.ParseDmsToDegrees(currentVal);
            decDeg += deltaDegrees;
            txtBearing.Text = CadMath.DegreesToDmsString(decDeg);
            
            _doc.Editor.WriteMessage($"\n[Bearing] Adjusted by {deltaDegrees:+#;-#;0}\u00B0");

            txtBearing.Focus();
            txtBearing.SelectAll();
        }
        else
        {
            _doc.Editor.WriteMessage("\n[Error] Invalid Bearing Format.");
        }
    }

    private void PanToPoint(Point3d target)
    {
        var ed = _doc.Editor;
        using (ViewTableRecord view = ed.GetCurrentView())
        {
            Matrix3d matWCS2DCS = Matrix3d.PlaneToWorld(view.ViewDirection) * Matrix3d.Displacement(view.Target - Point3d.Origin) * Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target);
            matWCS2DCS = matWCS2DCS.Inverse();
            Point3d centerPt = target.TransformBy(matWCS2DCS);
            view.CenterPoint = new Point2d(centerPt.X, centerPt.Y);
            ed.SetCurrentView(view);
        }
    }
    #endregion

    #region Secondary Modal Windows
    private void AddTextComment(string? preDefinedComment)
    {
        if (!_hasStartPoint) return;
        if (!ValidateDocument()) return;

        string finalComment = "";
        if (preDefinedComment != null) finalComment = preDefinedComment;
        else
        {
            CommentWpfWindow cWin = new CommentWpfWindow();
            cWin.Owner = System.Windows.Window.GetWindow(this);
            if (cWin.ShowDialog() == true) finalComment = cWin.Comment;
        }

        if (!string.IsNullOrWhiteSpace(finalComment))
        {
            using (DocumentLock loc = _doc.LockDocument())
            using (Transaction tr = _doc.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                TextSettings commSettings = new TextSettings { Style = "ROMANS80", Size = 2.5 };
                Entity txt = CreateText(finalComment, CadConstants.SYMB_TEXT, _lastCreatedVertex, AttachmentPoint.MiddleLeft, tr, _doc.Database, commSettings);
                ObjectId txtId = AddToDb(txt, btr, tr);
                if (_undoStack.Count > 0) _undoStack.Peek().Add(txtId);
                tr.Commit(); _doc.Editor.UpdateScreen();
            }
        }
        
        System.Windows.Window.GetWindow(this)?.Activate();
        ReturnToBearing();
    }

    private void OpenSideShotForm()
    {
        SideShotWpfWindow ssWin = new SideShotWpfWindow(this, txtBearing.Text, (brg, dist, comm) => 
        {
            if (!ValidateDocument()) return;
            ExecuteUiAction(() => {
                string finalDist = dist;
                if (_feetMode && double.TryParse(dist, out double feetVal))
                {
                    double meterVal = feetVal * 0.3048;
                    finalDist = meterVal.ToString("0.000");
                    lblDistanceTrace.Text = $"Side Shot: {feetVal:F3} ft = {meterVal:F3} m";
                }

                using (DocumentLock loc = _doc.LockDocument())
                using (Transaction tr = _doc.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    DrawGeometryToDatabase(tr, btr, brg, finalDist, _currentPoint, _currentLayer);
                    if (!string.IsNullOrWhiteSpace(comm))
                    {
                        double rawBrg; 
                        CadMath.TryParseBearing(brg, out rawBrg);
                        double distVal = double.Parse(finalDist);
                        double angleDeg = CadMath.ParseDmsToDegrees(rawBrg);
                        double rad = (90.0 - angleDeg) * (Math.PI / 180.0);
                        Point3d endPt = new Point3d(_currentPoint.X + (distVal * Math.Cos(rad)), _currentPoint.Y + (distVal * Math.Sin(rad)), _currentPoint.Z);
                        TextSettings commSettings = new TextSettings { Style = "ROMANS80", Size = 2.5 };
                        Entity txt = CreateText(comm, CadConstants.SYMB_TEXT, endPt, AttachmentPoint.MiddleLeft, tr, _doc.Database, commSettings);
                        ObjectId txtId = AddToDb(txt, btr, tr);
                        if (_undoStack.Count > 0) _undoStack.Peek().Add(txtId);
                    }
                    tr.Commit(); _doc.Editor.UpdateScreen();
                }
            });
        });
        ssWin.Owner = System.Windows.Window.GetWindow(this);
        ssWin.ShowDialog();
        ReturnToBearing();
    }

    private void OpenCalculator(TextBox txt, bool isDms)
    {
        CalculatorWindow cWin = new CalculatorWindow(txt.Text, isDms);
        cWin.Owner = System.Windows.Window.GetWindow(this);
        if (cWin.ShowDialog() == true) { txt.Text = cWin.Result; txt.Focus(); txt.SelectAll(); }
    }

    private void ExecuteScreenPick()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var ed = doc.Editor;

        this.Visibility = System.Windows.Visibility.Collapsed;
        System.Windows.Forms.Application.DoEvents();
        
        PromptPointResult ppr = ed.GetPoint("\nPick Start Point: ");
        
        this.Visibility = System.Windows.Visibility.Visible;
        System.Windows.Window.GetWindow(this)?.Activate();
 
        if (ppr.Status == PromptStatus.OK)
        {
            SetStartPoint(ppr.Value);
        }
        
        ReturnToBearing();
    }

    private void TriggerCoordsWindow()
    {
        this.Visibility = System.Windows.Visibility.Collapsed;
        System.Windows.Forms.Application.DoEvents();

        CoordsInputWindow w = new CoordsInputWindow(); w.Owner = System.Windows.Window.GetWindow(this);
        bool? res = w.ShowDialog();
        
        this.Visibility = System.Windows.Visibility.Visible;
 
        if (res == true)
        {
            if (w.PickRequested)
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    this.Visibility = System.Windows.Visibility.Collapsed;
                    System.Windows.Forms.Application.DoEvents();
 
                    PromptPointResult ppr = doc.Editor.GetPoint("\nPick Start Point: ");
                    
                    this.Visibility = System.Windows.Visibility.Visible;
 
                    if (ppr.Status == PromptStatus.OK)
                    {
                        SetStartPoint(ppr.Value);
                    }
                }
            }
            else
            {
                SetStartPoint(w.ResultPoint);
            }
        }
        
        System.Windows.Window.GetWindow(this)?.Activate();
        ReturnToBearing();
    }
 
    private void UpdateHistoryUi()
    {
        if (pnlHistory == null || lblStats == null) return;
        
        pnlHistory.Children.Clear();
        
        if (_segmentHistory.Count == 0)
        {
            lblStats.Text = _hasStartPoint 
                ? $"Origin: ({_traversePath[0].X:F2}, {_traversePath[0].Y:F2})" 
                : "No segments recorded yet.";
            return;
        }
        
        double totalDist = 0;
        foreach (var seg in _segmentHistory)
        {
            if (double.TryParse(seg.Distance, out double d)) totalDist += d;
        }
        
        lblStats.Text = $"Courses: {_segmentHistory.Count}  |  Perimeter: {totalDist:F3}m\nOrigin: ({_traversePath[0].X:F2}, {_traversePath[0].Y:F2})";
        
        // Column Header Grid
        Grid headerGrid = new Grid() { Margin = new Thickness(0, 2, 0, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(24) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(65) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(85) });
        
        headerGrid.Children.Add(new TextBlock() { Text = "#", FontWeight = FontWeights.Bold, Foreground = Brushes.Gray, FontSize = 10 });
        headerGrid.Children.Add(new TextBlock() { Text = "BEARING", FontWeight = FontWeights.Bold, Foreground = Brushes.Gray, FontSize = 10 });
        headerGrid.Children.Add(new TextBlock() { Text = "DIST (m)", FontWeight = FontWeights.Bold, Foreground = Brushes.Gray, FontSize = 10 });
        headerGrid.Children.Add(new TextBlock() { Text = "LAYER", FontWeight = FontWeights.Bold, Foreground = Brushes.Gray, FontSize = 10 });
        
        Grid.SetColumn(headerGrid.Children[0], 0);
        Grid.SetColumn(headerGrid.Children[1], 1);
        Grid.SetColumn(headerGrid.Children[2], 2);
        Grid.SetColumn(headerGrid.Children[3], 3);
        
        pnlHistory.Children.Add(headerGrid);
        
        for (int i = 0; i < _segmentHistory.Count; i++)
        {
            var seg = _segmentHistory[i];
            
            Grid row = new Grid() { 
                Background = i % 2 == 0 ? new SolidColorBrush(Color.FromRgb(35, 35, 35)) : new SolidColorBrush(Color.FromRgb(42, 42, 42)),
                Margin = new Thickness(0, 1, 0, 1)
            };
            
            row.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(65) });
            row.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(85) });
            
            var tbIdx = new TextBlock() { Text = seg.Index.ToString(), Foreground = Brushes.Gray, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 3, 4, 3) };
            var tbBrg = new TextBlock() { Text = seg.Bearing, Foreground = Brushes.Cyan, FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 3, 4, 3) };
            var tbDist = new TextBlock() { Text = seg.Distance, Foreground = Brushes.White, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 3, 4, 3) };
            Border pill = new Border() { 
                CornerRadius = new CornerRadius(2), 
                Padding = new Thickness(3, 1, 3, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60))
            };
            var tbLay = new TextBlock() { Text = seg.Layer.Replace("BOUNDARY_", "").Replace("CONNECTION_", ""), FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            pill.Child = tbLay;
            
            try
            {
                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(_doc.Database.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(seg.Layer))
                    {
                        LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(lt[seg.Layer], OpenMode.ForRead);
                        var sysCol = ltr.Color.ColorValue;
                        pill.Background = new SolidColorBrush(Color.FromRgb(sysCol.R, sysCol.G, sysCol.B));
                        double brt = (sysCol.R * 0.299 + sysCol.G * 0.587 + sysCol.B * 0.114);
                        tbLay.Foreground = (brt > 128) ? Brushes.Black : Brushes.White;
                    }
                    tr.Commit();
                }
            }
            catch {}
            
            Grid.SetColumn(tbIdx, 0); row.Children.Add(tbIdx);
            Grid.SetColumn(tbBrg, 1); row.Children.Add(tbBrg);
            Grid.SetColumn(tbDist, 2); row.Children.Add(tbDist);
            Grid.SetColumn(pill, 3); row.Children.Add(pill);
            
            pnlHistory.Children.Add(row);
        }
        
        svHistory.ScrollToEnd();
    }
    #endregion

    #region Annotation Tools
    private void ExecuteSwapText()
    {
        if (!ValidateDocument()) return;
        var ed = _doc.Editor;

        this.Visibility = System.Windows.Visibility.Collapsed;
        System.Windows.Forms.Application.DoEvents();

        try
        {
            while (true)
            {
                PromptEntityOptions peo = new PromptEntityOptions("\nSelect boundary line to swap text (or press ESC to exit): ");
                peo.SetRejectMessage("\nOnly lines can be selected.");
                peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), false);
                PromptEntityResult per = ed.GetEntity(peo);

                if (per.Status == PromptStatus.Cancel) break;
                if (per.Status != PromptStatus.OK) continue;

                using (DocumentLock loc = _doc.LockDocument())
                using (Transaction tr = _doc.TransactionManager.StartTransaction())
                {
                    Autodesk.AutoCAD.DatabaseServices.Line selLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                    BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    string[] bearingLayers = { CadConstants.BDY_BEARING, CadConstants.CONNECTION_BEAR, "BEAR" };
                    string[] distanceLayers = { CadConstants.BDY_DISTANCE, CadConstants.CONNECTION_DIST, "DIM" };

                    Entity bearingText = null;
                    Entity distText = null;
                    double tolerance = GetModelSize(5.0);
                    Point3d mid = selLine.StartPoint + (selLine.EndPoint - selLine.StartPoint) / 2.0;

                    foreach (ObjectId id in btr)
                    {
                        Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                        if (ent is DBText || ent is MText)
                        {
                            Point3d tPos = (ent is DBText dbt) ? ((dbt.Justify == AttachmentPoint.BaseLeft) ? dbt.Position : dbt.AlignmentPoint) : ((MText)ent).Location;
                            if (tPos.DistanceTo(mid) < tolerance)
                            {
                                if (bearingLayers.Contains(ent.Layer)) bearingText = ent;
                                else if (distanceLayers.Contains(ent.Layer)) distText = ent;
                            }
                        }
                    }

                    if (bearingText != null && distText != null)
                    {
                        bearingText.UpgradeOpen();
                        distText.UpgradeOpen();

                        Point3d brgCoord = (bearingText is DBText dbtB) ? dbtB.AlignmentPoint : ((MText)bearingText).Location;
                        Point3d distCoord = (distText is DBText dbtD) ? dbtD.AlignmentPoint : ((MText)distText).Location;
                        AttachmentPoint brgJust = (bearingText is DBText dB) ? dB.Justify : ((MText)bearingText).Attachment;
                        AttachmentPoint distJust = (distText is DBText dD) ? dD.Justify : ((MText)distText).Attachment;

                        // Condition A: Standard -> Swapped
                        if (brgJust == AttachmentPoint.BottomCenter)
                        {
                            UpdateTextJustAndPos(bearingText, AttachmentPoint.TopCenter, distCoord);
                            UpdateTextJustAndPos(distText, AttachmentPoint.BottomCenter, brgCoord);
                        }
                        // Condition B: Swapped -> Standard
                        else
                        {
                            UpdateTextJustAndPos(bearingText, AttachmentPoint.BottomCenter, distCoord);
                            UpdateTextJustAndPos(distText, AttachmentPoint.TopCenter, brgCoord);
                        }

                        tr.Commit();
                        ed.UpdateScreen();
                    }
                }
            }
        }
        finally
        {
            this.Visibility = System.Windows.Visibility.Visible;
            System.Windows.Window.GetWindow(this)?.Activate();
            ReturnToBearing();
        }
    }

    private void UpdateTextJustAndPos(Entity ent, AttachmentPoint just, Point3d pos)
    {
        if (ent is DBText dbt)
        {
            dbt.Justify = just;
            dbt.AlignmentPoint = pos;
        }
        else if (ent is MText mt)
        {
            mt.Attachment = just;
            mt.Location = pos;
        }
    }

    private void RotateBearingText()
    {
        if (!ValidateDocument()) return;
        var ed = _doc.Editor;

        try
        {
            while (true)
            {
                this.Visibility = System.Windows.Visibility.Collapsed;
                System.Windows.Forms.Application.DoEvents();

                PromptEntityOptions peo = new PromptEntityOptions("\nSelect bearing TEXT to flip 180\u00B0 (or press ESC to exit): ");
                peo.SetRejectMessage("\nOnly text objects can be selected.");
                peo.AddAllowedClass(typeof(DBText), false);
                peo.AddAllowedClass(typeof(MText), false);
                PromptEntityResult per = ed.GetEntity(peo);

                if (per.Status == PromptStatus.Cancel) break;
                if (per.Status != PromptStatus.OK) continue;

                using (DocumentLock loc = _doc.LockDocument())
                using (Transaction tr = _doc.TransactionManager.StartTransaction())
                {
                    Entity ent = (Entity)tr.GetObject(per.ObjectId, OpenMode.ForWrite);

                    // Strict Target Validation
                    bool isBearingLayer = (ent.Layer == CadConstants.BDY_BEARING || ent.Layer == CadConstants.CONNECTION_BEAR || string.Equals(ent.Layer, "BEAR", StringComparison.OrdinalIgnoreCase));
                    if (!isBearingLayer)
                    {
                        ed.WriteMessage("\n[Error] Please select TEXT on a Bearing layer.");
                        continue;
                    }

                    string rawTxt = (ent is DBText dbt) ? dbt.TextString : ((MText)ent).Contents;

                    // Math Logic (Degree-Only Modification)
                    var match = System.Text.RegularExpressions.Regex.Match(rawTxt, @"^(\d+)");
                    if (match.Success)
                    {
                        string degStr = match.Groups[1].Value;
                        if (int.TryParse(degStr, out int oldDeg))
                        {
                            int newDeg = (oldDeg + 180) % 360;
                            string newTxt = newDeg.ToString() + rawTxt.Substring(degStr.Length);

                            if (ent is DBText d) d.TextString = newTxt;
                            else if (ent is MText m) m.Contents = newTxt;

                            tr.Commit();
                            ed.UpdateScreen();
                        }
                    }
                }
            }
        }
        finally
        {
            this.Visibility = System.Windows.Visibility.Visible;
            System.Windows.Window.GetWindow(this)?.Activate();
            ReturnToBearing();
        }
    }

    private void AnnotateSelectedLine()
    {
        if (!ValidateDocument()) return;
        var ed = _doc.Editor;

        try
        {
            while (true)
            {
                this.Visibility = System.Windows.Visibility.Collapsed;
                System.Windows.Forms.Application.DoEvents();

                PromptEntityOptions peo = new PromptEntityOptions("\nSelect line to annotate (or press ESC to exit): ");
                peo.SetRejectMessage("\nOnly lines can be selected.");
                peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), false);
                PromptEntityResult per = ed.GetEntity(peo);

                if (per.Status == PromptStatus.Cancel) break;
                if (per.Status != PromptStatus.OK) continue;

                using (DocumentLock loc = _doc.LockDocument())
                using (Transaction tr = _doc.TransactionManager.StartTransaction())
                {
                    Autodesk.AutoCAD.DatabaseServices.Line selLine = (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(per.ObjectId, OpenMode.ForRead);

                    double dist = selLine.Length;
                    double cadAngleRad = selLine.Angle;

                    double angleDeg = 90.0 - (cadAngleRad * 180.0 / Math.PI);
                    if (angleDeg < 0) angleDeg += 360.0;

                    double rawBrg = double.Parse(CadMath.DegreesToDmsString(angleDeg));

                    BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    string originalLayer = _currentLayer;
                    _currentLayer = selLine.Layer;

                    List<ObjectId> created = CreateAnnotatedText(btr, tr, selLine, rawBrg, dist, cadAngleRad);
                    if (_undoStack.Count > 0) _undoStack.Peek().AddRange(created);
                    else _undoStack.Push(created);

                    _currentLayer = originalLayer;

                    tr.Commit();
                    ed.UpdateScreen();
                    ed.WriteMessage("\n[Annotate] Line annotated.");
                }
            }
        }
        finally
        {
            this.Visibility = System.Windows.Visibility.Visible;
            System.Windows.Window.GetWindow(this)?.Activate();
            ReturnToBearing();
        }
    }

    private void ApplyQLDStandards()
    {
        if (!ValidateDocument()) return;
        var ed = _doc.Editor;
        int lCount = 0;
        int tCount = 0;
        int s3Count = 0;
        int s4Count = 0;
        int s5Count = 0;
        int s6Count = 0;

        using (DocumentLock loc = _doc.LockDocument())
        using (Transaction tr = _doc.TransactionManager.StartTransaction())
        {
            LayerTable lt = (LayerTable)tr.GetObject(_doc.Database.LayerTableId, OpenMode.ForRead);
            LinetypeTable ltt = (LinetypeTable)tr.GetObject(_doc.Database.LinetypeTableId, OpenMode.ForRead);

            // 1. Robust Layer Creation (Ensuring QLD layers exist with correct properties)
            EnsureLayer(lt, ltt, "70", 1, tr, "Continuous");    // Red
            EnsureLayer(lt, ltt, "35", 2, tr, "Continuous");    // Yellow
            EnsureLayer(lt, ltt, "TRAV", 4, tr, "TRAV");        // Cyan
            EnsureLayer(lt, ltt, "AABT", 2, tr, "ABT");         // Yellow
            EnsureLayer(lt, ltt, "BEAR", 2, tr, "Continuous");  // Yellow
            EnsureLayer(lt, ltt, "DIM", 2, tr, "Continuous");   // Yellow
            EnsureLayer(lt, ltt, "STNO", 2, tr, "Continuous");  // Yellow
            EnsureLayer(lt, ltt, "CORINF", 4, tr, "Continuous"); // Cyan

            // 1.5 Ensure QLD Text Styles Exist (Stage 5)
            ObjectId styleSU = EnsureTextStyle(tr, "SU", _doc.Database);
            ObjectId styleSS = EnsureTextStyle(tr, "SS", _doc.Database);

            // 2. Entity Processing Loop (ModelSpace sweep)
            BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var lineMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "BOUNDARY_SUBJECT", "70" },
                { "BOUNDARY_ADJOINING", "35" },
                { "CONNECTIONS", "TRAV" },
                { "BDY_EASEMENT", "AABT" }
            };

            var textMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { CadConstants.BDY_BEARING, "BEAR" },
                { CadConstants.CONNECTION_BEAR, "BEAR" },
                { CadConstants.BDY_DISTANCE, "DIM" },
                { CadConstants.CONNECTION_DIST, "DIM" },
                { CadConstants.POINT_NUMBER, "STNO" },
                { CadConstants.SYMB_TEXT, "CORINF" }
            };

            foreach (ObjectId id in btr)
            {
                Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);

                // For Lines (Full namespace used to avoid conflict with System.Windows.Shapes.Line)
                if (ent is Autodesk.AutoCAD.DatabaseServices.Line ln)
                {
                    if (lineMap.TryGetValue(ln.Layer, out string newLineLayer))
                    {
                        ln.UpgradeOpen();
                        ln.Layer = newLineLayer;
                        lCount++;
                    }
                }
                // For Text (DBText only with safe position preservation and Stage 3/4/5/6 processing)
                else if (ent is DBText dbt)
                {
                    bool moved = textMap.TryGetValue(dbt.Layer, out string newTextLayer);
                    string targetLayer = moved ? newTextLayer : dbt.Layer;

                    bool isBear = string.Equals(targetLayer, "BEAR", StringComparison.OrdinalIgnoreCase);
                    bool isDim = string.Equals(targetLayer, "DIM", StringComparison.OrdinalIgnoreCase);
                    bool isStno = string.Equals(targetLayer, "STNO", StringComparison.OrdinalIgnoreCase);
                    bool isCorinf = string.Equals(targetLayer, "CORINF", StringComparison.OrdinalIgnoreCase);

                    if (moved || isBear || isDim || isStno || isCorinf)
                    {
                        string oldText = dbt.TextString;
                        string formattedText = oldText;

                        // Stage 3: Truncation logic
                        if (isBear) formattedText = FormatBearingNT(oldText);
                        else if (isDim) formattedText = FormatDistanceQLD(oldText);

                        bool stage3Changed = (formattedText != oldText);

                        // Stage 6: Symbol Replacement logic
                        string finalString = formattedText;
                        if (isBear)
                        {
                            // Strip existing codes first to avoid 'double-coding' (like %%%%d)
                            finalString = finalString.Replace("%%d", "°").Replace("%%D", "°")
                                                     .Replace("%%135", "'")
                                                     .Replace("%%136", "\"");

                            // Re-apply standard AutoCAD symbols
                            finalString = finalString.Replace("°", "%%d")
                                                     .Replace("'", "%%135")
                                                     .Replace("\"", "%%136");
                        }

                        bool stage6Changed = (finalString != formattedText);
                        bool textChanged = (finalString != oldText);

                        // Stage 4: Scaling logic
                        double baseSize = CadConstants.GetReferenceHeight(targetLayer ?? "");
                        double targetHeight = GetModelSize(baseSize);
                        bool heightChanged = (Math.Abs(dbt.Height - targetHeight) > 0.0001);

                        // Stage 5: Style & Obliquing logic
                        ObjectId targetStyle = ObjectId.Null;
                        double targetOblique = 0.0;

                        if (isBear || isStno)
                        {
                            targetStyle = styleSU;
                            targetOblique = 0.0;
                        }
                        else if (isDim || isCorinf)
                        {
                            targetStyle = styleSS;
                            targetOblique = 20.0 * (Math.PI / 180.0); // 20 degrees
                        }

                        bool styleChanged = (targetStyle != ObjectId.Null && dbt.TextStyleId != targetStyle);
                        bool obliqueChanged = (targetStyle != ObjectId.Null && Math.Abs(dbt.Oblique - targetOblique) > 0.0001);

                        if (moved || textChanged || heightChanged || styleChanged || obliqueChanged)
                        {
                            // Capture justification and position first to ensure stability
                            AttachmentPoint justification = dbt.Justify;
                            Point3d preservedPt = (justification == AttachmentPoint.BaseLeft) ? dbt.Position : dbt.AlignmentPoint;

                            dbt.UpgradeOpen();
                            
                            if (moved)
                            {
                                dbt.Layer = targetLayer;
                                tCount++;
                            }

                            if (textChanged)
                            {
                                dbt.TextString = finalString;
                                if (stage3Changed) s3Count++;
                                if (stage6Changed) s6Count++;
                            }

                            if (heightChanged)
                            {
                                dbt.Height = targetHeight;
                                s4Count++;
                            }

                            if (styleChanged)
                            {
                                dbt.TextStyleId = targetStyle;
                            }

                            if (obliqueChanged)
                            {
                                dbt.Oblique = targetOblique;
                            }

                            if (styleChanged || obliqueChanged)
                            {
                                s5Count++;
                            }

                            // Re-apply preserved position based on justification to prevent eNotApplicable
                            if (justification == AttachmentPoint.BaseLeft)
                                dbt.Position = preservedPt;
                            else
                                dbt.AlignmentPoint = preservedPt;
                        }
                    }
                }
            }

            tr.Commit();
            ed.WriteMessage($"\n[QLD] Updated {lCount} lines and {tCount} text objects.");
            ed.WriteMessage($"\n[QLD Stage 3] Truncation complete. {s3Count} labels formatted.");
            ed.WriteMessage($"\n[QLD Stage 4] Scaling complete. {s4Count} labels resized to match 1:{_plotScale}.");
            ed.WriteMessage($"\n[QLD Stage 5] Styles and Obliquing (20°) applied to {s5Count} labels.");
            ed.WriteMessage($"\n[QLD Fix] Symbol codes corrected (%%d used for degrees).");
            ed.UpdateScreen();
        }
        ReturnToBearing();
    }

    private string FormatDistanceQLD(string input)
    {
        Match m = Regex.Match(input, @"^([\d.|]+)\s*(.*)$");
        if (m.Success)
        {
            string numPart = m.Groups[1].Value;
            string suffixPart = m.Groups[2].Value;

            string normalizedNumber = numPart.Replace('|', '.');

            if (decimal.TryParse(normalizedNumber, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal val))
            {
                string formattedNum = val.ToString("0.0####", CultureInfo.InvariantCulture).Replace('.', '|');
                return formattedNum + suffixPart;
            }
        }
        return input;
    }

    private string RemoveTrailingZerosNT(string input)
    {
        Match m = Regex.Match(input, @"^([\d.]+)\s*(.*)$");
        if (m.Success)
        {
            string numPart = m.Groups[1].Value;
            string suffixPart = m.Groups[2].Value;

            if (double.TryParse(numPart, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
            {
                // Round to the closest 5mm (0.005) using Bankers' Rounding (ToEven)
                double roundedVal = Math.Round(val / 0.005, MidpointRounding.ToEven) * 0.005;
                
                // Format with up to 3 decimal places
                string formattedNum = roundedVal.ToString("0.000", CultureInfo.InvariantCulture);
                
                // Strip trailing zeros from the decimal part
                if (formattedNum.Contains("."))
                {
                    while (formattedNum.EndsWith("0"))
                    {
                        formattedNum = formattedNum.Substring(0, formattedNum.Length - 1);
                    }
                    if (formattedNum.EndsWith("."))
                    {
                        formattedNum = formattedNum.Substring(0, formattedNum.Length - 1);
                    }
                }
                
                if (roundedVal == 1.0)
                {
                    formattedNum = "1.0";
                }

                return formattedNum + suffixPart;
            }
        }
        return input;
    }

    private string FormatBearingNT(string input)
    {
        // Normalize symbols for parsing
        string normalized = input.Replace("%%d", "°").Replace("%%D", "°")
                                 .Replace("%%135", "'")
                                 .Replace("%%136", "\"");

        // 1. Try to parse degree, minutes, seconds and suffix
        Match match3 = Regex.Match(normalized, @"^(\d+)[°\u00B0](\d+)'(\d+)\""(\s*.*)$");
        Match match2 = Regex.Match(normalized, @"^(\d+)[°\u00B0](\d+)'(\s*.*)$");
        Match match1 = Regex.Match(normalized, @"^(\d+)[°\u00B0](\s*.*)$");

        int d = 0, m = 0, s = 0;
        string suffix = "";
        bool parsed = false;

        if (match3.Success)
        {
            d = int.Parse(match3.Groups[1].Value);
            m = int.Parse(match3.Groups[2].Value);
            s = int.Parse(match3.Groups[3].Value);
            suffix = match3.Groups[4].Value;
            parsed = true;

            // Rounding logic for seconds: round to closest 10", Banker's rounding for exactly 5"
            int lower = (s / 10) * 10;
            int upper = lower + 10;
            int roundedS;

            if (s % 10 == 5)
            {
                int coeff = s / 10;
                if (coeff % 2 == 0) roundedS = lower;
                else roundedS = upper;
            }
            else if (s % 10 < 5)
            {
                roundedS = lower;
            }
            else
            {
                roundedS = upper;
            }

            if (roundedS == 60)
            {
                roundedS = 0;
                m++;
                if (m == 60)
                {
                    m = 0;
                    d = (d + 1) % 360;
                }
            }
            s = roundedS;
        }
        else if (match2.Success)
        {
            d = int.Parse(match2.Groups[1].Value);
            m = int.Parse(match2.Groups[2].Value);
            s = 0;
            suffix = match2.Groups[3].Value;
            parsed = true;
        }
        else if (match1.Success)
        {
            d = int.Parse(match1.Groups[1].Value);
            m = 0;
            s = 0;
            suffix = match1.Groups[2].Value;
            parsed = true;
        }

        if (parsed)
        {
            bool isQldText = input.Contains("%%d") || input.Contains("%%D") || input.Contains("%%135") || input.Contains("%%136");
            string degSym = isQldText ? "%%d" : "\u00B0";
            string minSym = isQldText ? "%%135" : "'";
            string secSym = isQldText ? "%%136" : "\"";

            string resultBrg = $"{d}{degSym}{m:00}{minSym}{s:00}{secSym}";

            // Truncate 00" / %%136
            resultBrg = resultBrg.Replace("00\"", "").Replace("00%%136", "");

            // Truncate 00' / %%135 if it's now at the end of the bearing digits
            if (resultBrg.EndsWith("00'") || resultBrg.EndsWith("00%%135"))
            {
                resultBrg = resultBrg.Substring(0, resultBrg.Length - (resultBrg.EndsWith("00'") ? 3 : 7));
            }

            return resultBrg + suffix;
        }

        // Fallback to simple replace if it didn't match the standard formats
        string result = input.Replace("00\"", "").Replace("00%%136", "");
        if (result.EndsWith("00'") || result.EndsWith("00%%135"))
        {
            result = result.Substring(0, result.Length - (result.EndsWith("00'") ? 3 : 7));
        }
        return result;
    }

    private void ApplyNTStandards()
    {
        if (!ValidateDocument()) return;
        var ed = _doc.Editor;
        int updateCount = 0;

        using (DocumentLock loc = _doc.LockDocument())
        using (Transaction tr = _doc.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(_doc.Database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            string[] bearingLayers = { CadConstants.BDY_BEARING, CadConstants.CONNECTION_BEAR };
            string[] distanceLayers = { CadConstants.BDY_DISTANCE, CadConstants.CONNECTION_DIST };

            foreach (ObjectId id in btr)
            {
                Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                
                if (ent is DBText dbt)
                {
                    bool isBearing = bearingLayers.Contains(ent.Layer);
                    bool isDistance = distanceLayers.Contains(ent.Layer);
                    
                    if (!isBearing && !isDistance) continue;

                    string oldText = dbt.TextString;
                    string newText = oldText;

                    if (isBearing)
                    {
                        newText = FormatBearingNT(oldText);
                    }
                    else if (isDistance)
                    {
                        newText = RemoveTrailingZerosNT(oldText);
                    }

                    if (newText != oldText)
                    {
                        ent.UpgradeOpen();
                        
                        Point3d anchor = dbt.AlignmentPoint;
                        dbt.TextString = newText;
                        dbt.AlignmentPoint = anchor;
                        
                        updateCount++;
                    }
                }
            }
            tr.Commit();
        }

        ed.WriteMessage($"\n[NT Format] Drawing sweep complete. Updated {updateCount} labels.");
        ed.UpdateScreen();
        ed.Regen();
        ReturnToBearing();
    }
    #endregion
}
#endregion

#region 7. SECONDARY DIALOGS
// --- DMS CALCULATOR WINDOW ---
public class CalculatorWindow : System.Windows.Window
{
    public string Result = "";
    private TextBox txtInput;
    private bool _isDms;
    public CalculatorWindow(string initial, bool isDms)
    {
        _isDms = isDms;
        this.Title = isDms ? "DMS Calc (+/-)" : "Calc"; this.Width = 300; this.Height = 150;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Background = UITheme.BackgroundBrush;
        Grid g = new Grid(); g.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) }); g.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
        txtInput = UITheme.CreateInputBox(); txtInput.Text = initial; txtInput.Margin = new Thickness(10);
        txtInput.KeyDown += (s, e) => { if (e.Key == Key.Enter) DoCalc(); };
        Button btnOk = new Button() { Content = "=", Height = 40, Background = Brushes.Cyan, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 12 }; btnOk.Click += (s, e) => DoCalc();
        g.Children.Add(txtInput); Grid.SetRow(txtInput, 0); g.Children.Add(btnOk); Grid.SetRow(btnOk, 1);
        this.Content = g; this.Loaded += (s, e) => { txtInput.Focus(); txtInput.Select(txtInput.Text.Length, 0); };
    }
    private void DoCalc()
    {
        try
        {
            if (_isDms)
            {
                string[] parts;
                if (txtInput.Text.Contains("+")) { parts = txtInput.Text.Split('+'); Result = CadMath.DmsToString(CadMath.AddSubDms(double.Parse(parts[0]), double.Parse(parts[1]), true)); }
                else if (txtInput.Text.Contains("-")) { parts = txtInput.Text.Split('-'); Result = CadMath.DmsToString(CadMath.AddSubDms(double.Parse(parts[0]), double.Parse(parts[1]), false)); }
                else Result = txtInput.Text;
            }
            else
            {
                System.Data.DataTable dt = new System.Data.DataTable(); var v = dt.Compute(txtInput.Text, ""); Result = v.ToString();
            }
            this.DialogResult = true; this.Close();
        }
        catch { MessageBox.Show("Invalid Math"); }
    }
}

// --- COORDS INPUT WINDOW ---
public class CoordsInputWindow : System.Windows.Window
{
    public Point3d ResultPoint;
    public bool PickRequested = false;
    private TextBox txtE, txtN;
    public CoordsInputWindow()
    {
        this.Title = "Start Point"; this.Width = 400; this.Height = 300;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Background = UITheme.BackgroundBrush;
        
        this.PreviewKeyDown += (s, e) => {
            if (e.Key == Key.Escape) { this.DialogResult = false; this.Close(); e.Handled = true; }
        };

        StackPanel sp = new StackPanel() { Margin = new Thickness(20) };
        sp.Children.Add(UITheme.CreateLabel("EASTING (X)"));
        txtE = UITheme.CreateInputBox(); sp.Children.Add(txtE);
        sp.Children.Add(UITheme.CreateLabel("NORTHING (Y)"));
        txtN = UITheme.CreateInputBox(); sp.Children.Add(txtN);

        txtE.KeyDown += (s, e) => { if (e.Key == Key.Enter) txtN.Focus(); };
        txtN.KeyDown += (s, e) => { if (e.Key == Key.Enter) Submit(); };

        Button btnPick = new Button() { Content = UITheme.CreateShortcutContent("PgDn", "\ud83d\uddb1\ufe0f PICK"), Height = 45, Margin = new Thickness(0, 10, 0, 0), Background = UITheme.ActionBlue, Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Stretch };
        btnPick.Click += (s, e) => {
            PickRequested = true;
            this.DialogResult = true;
            this.Close();
        };
        sp.Children.Add(btnPick);

        this.Content = sp;
        this.Loaded += (s, e) => txtE.Focus();
    }

    private void Submit()
    {
        if (double.TryParse(txtE.Text, out double x) && double.TryParse(txtN.Text, out double y))
        {
            ResultPoint = new Point3d(x, y, 0);
            PickRequested = false;
            this.DialogResult = true;
            this.Close();
        }
        else
        {
            MessageBox.Show("Please enter valid numeric coordinates.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public class SideShotWpfWindow : System.Windows.Window
{
    private TextBox txtBrg = null!, txtDist = null!, txtComm = null!;
    private TextBlock lblBrgTrace = null!, lblDistTrace = null!;
    private Action<string, string, string> _onAddLine;
    private CadastreWpfWindow _parent;
 
    public SideShotWpfWindow(CadastreWpfWindow parent, string initialBearing, Action<string, string, string> onAddLine)
    {
        _parent = parent;
        _onAddLine = onAddLine;
        this.Title = "SIDE SHOT"; this.Width = 450; this.Height = 550;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Background = UITheme.BackgroundBrush; this.ResizeMode = ResizeMode.NoResize;

        this.PreviewKeyDown += (s, e) => {
            if (e.Key == Key.Escape) { this.Close(); e.Handled = true; }
        };

        Grid root = new Grid(); root.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
        Border header = new Border() { Background = UITheme.CardBrush, Padding = new Thickness(10) };
        header.Child = new TextBlock() { Text = "SIDE SHOT", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetRow(header, 0); root.Children.Add(header);

        Border card = UITheme.CreateCard(); card.Margin = new Thickness(12); StackPanel pnl = new StackPanel();

        pnl.Children.Add(UITheme.CreateLabel("BEARING"));
        txtBrg = UITheme.CreateInputBox(); txtBrg.PreviewKeyDown += Input_PreviewKeyDown;
        pnl.Children.Add(txtBrg);
        lblBrgTrace = new TextBlock() { FontSize = 13, Foreground = Brushes.LightGray, FontStyle = FontStyles.Italic, FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 2, 0, 8) };
        pnl.Children.Add(lblBrgTrace);

        pnl.Children.Add(UITheme.CreateLabel("DISTANCE"));
        txtDist = UITheme.CreateInputBox(); txtDist.PreviewKeyDown += Input_PreviewKeyDown;
        txtDist.GotFocus += (s, e) => { txtDist.BorderBrush = Brushes.WhiteSmoke; txtDist.BorderThickness = new Thickness(2); };
        txtDist.LostFocus += (s, e) => { txtDist.BorderBrush = Brushes.Gray; txtDist.BorderThickness = new Thickness(1); };
        pnl.Children.Add(txtDist);
        lblDistTrace = new TextBlock() { FontSize = 13, Foreground = Brushes.LightGray, FontStyle = FontStyles.Italic, FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 2, 0, 8) };
        pnl.Children.Add(lblDistTrace);

        pnl.Children.Add(UITheme.CreateLabel("COMMENT")); 
        txtComm = UITheme.CreateInputBox();
        txtComm.PreviewKeyDown += Input_PreviewKeyDown;
        pnl.Children.Add(txtComm);
        
        card.Child = pnl; Grid.SetRow(card, 1); root.Children.Add(card);

        Grid btns = new Grid() { Margin = new Thickness(12, 0, 12, 12) };
        Button btnExit = new Button() { Content = "EXIT", Height = 40, Background = UITheme.ActionBlue, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 12 };
        btnExit.Click += (s, e) => { this.Close(); };
        btns.Children.Add(btnExit); Grid.SetRow(btns, 2); root.Children.Add(btns);
        this.Content = root; 
        
        this.Loaded += (s, e) => 
        {
            txtBrg.Text = initialBearing;
            txtBrg.Focus();
            txtBrg.SelectAll();
        };
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        TextBox tb = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string input = tb.Text.Trim();
            
            if (input.Contains("+") || input.Contains("-") || input.Contains("*") || input.Contains("/"))
            {
                string oldVal = input;
                string? result = _parent.EvaluateInlineExpression(input, tb == txtBrg);
                if (result != null)
                {
                    tb.Text = result;
                    if (tb == txtBrg) lblBrgTrace.Text = $"{oldVal} = {result} ({CadMath.FormatAsSurveyor(double.Parse(result))})";
                    else if (tb == txtDist) lblDistTrace.Text = $"{oldVal} = {result}";
                    
                    tb.Foreground = Brushes.White; tb.FontWeight = FontWeights.Bold;
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    timer.Tick += (s, ev) => { tb.Foreground = Brushes.Cyan; tb.FontWeight = FontWeights.Normal; timer.Stop(); };
                    timer.Start();

                    tb.SelectAll();
                    return;
                }
            }

            if (tb == txtBrg) 
            {
                if (!CadMath.TryParseBearing(tb.Text, out double _))
                {
                    lblBrgTrace.Text = "";
                    tb.Focus();
                    tb.SelectAll();
                    return;
                }
                txtDist.Focus(); txtDist.SelectAll(); 
            }
            else if (tb == txtDist)
            {
                txtComm.Focus(); txtComm.SelectAll();
            }
            else if (tb == txtComm) 
            {
                if (!string.IsNullOrWhiteSpace(txtBrg.Text) && !string.IsNullOrWhiteSpace(txtDist.Text))
                {
                    _onAddLine?.Invoke(txtBrg.Text, txtDist.Text, txtComm.Text);
                    lblBrgTrace.Text = ""; lblDistTrace.Text = "";
                    txtBrg.Focus(); txtBrg.SelectAll();
                    txtDist.SelectAll();
                    txtComm.SelectAll();
                }
            }
        }
    }
}

public class CommentWpfWindow : System.Windows.Window
{
    public string Comment => txtComm.Text; private TextBox txtComm = null!;
    public CommentWpfWindow()
    {
        this.Title = "ADD COMMENT"; this.Width = 500; this.Height = 250;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Background = UITheme.BackgroundBrush; this.ResizeMode = ResizeMode.NoResize;
        Grid root = new Grid(); root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
        Border card = UITheme.CreateCard(); card.Margin = new Thickness(20); StackPanel pnl = new StackPanel();
        pnl.Children.Add(UITheme.CreateLabel("ENTER TEXT")); txtComm = UITheme.CreateInputBox();
        txtComm.KeyDown += (s, e) => { if (e.Key == Key.Enter) { this.DialogResult = true; this.Close(); } };
        pnl.Children.Add(txtComm); card.Child = pnl;
        Button btnOk = new Button() { Content = "OK", Width = 100, Height = 40, Background = UITheme.AccentColor, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 0, 0, 20) };
        btnOk.Click += (s, e) => { this.DialogResult = true; this.Close(); };
        Grid.SetRow(card, 0); Grid.SetRow(btnOk, 1);
        root.Children.Add(card); root.Children.Add(btnOk);
        this.Content = root; this.Loaded += (s, e) => txtComm.Focus();
    }
}

public class HelpWpfWindow : System.Windows.Window
{
    public HelpWpfWindow(string content)
    {
        this.Title = "TECHNICAL HELP MANUAL - CADASTRE LINES";
        this.Width = 550;
        this.Height = 650;
        this.Background = UITheme.BackgroundBrush;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.ShowInTaskbar = false;

        Grid root = new Grid();
        root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });

        ScrollViewer sv = new ScrollViewer() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(15) };
        TextBlock tb = new TextBlock() { 
            Text = content, 
            Foreground = Brushes.White, 
            TextWrapping = TextWrapping.Wrap, 
            FontFamily = new FontFamily("Segoe UI"), 
            FontSize = 13,
            LineHeight = 20
        };
        sv.Content = tb;
        Grid.SetRow(sv, 0);
        root.Children.Add(sv);

        Button btnClose = new Button() { Content = "CLOSE", Height = 40, Width = 100, Margin = new Thickness(0, 0, 0, 15), Background = UITheme.ActionBlue, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 12 };
        btnClose.Click += (s, e) => this.Close();
        Grid.SetRow(btnClose, 1);
        root.Children.Add(btnClose);

        this.Content = root;
    }
}
#endregion

