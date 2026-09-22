using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace LbaBodyStudio;

public sealed class MainForm : Form
{
    readonly TextBox imagePath=new(),game1=new(),game2=new(),output=new();
    readonly TextBox bandanaText=new(){MaxLength=8};
    readonly CheckBox headDetails=new(){Text="Add bandana, teeth and rear ties",AutoSize=true};
    readonly ComboBox target=Combo("Both","LBA1","LBA2"),layout=Combo("Front + back","Single front"),mask=Combo("Dark subject","Light subject","Transparent background","Background colour");
    readonly ComboBox method=Combo("New humanoid","Fit template");
    readonly NumericUpDown body1=Number(0,10000,0),body2=Number(0,10000,0),threshold=Number(1,254,45),fit=Number(0,100,80),height=Number(25,300,100),width=Number(25,300,100),depth=Number(25,300,100),head=Number(25,150,70),budget=Number(0,540,460);
    readonly CheckBox autoCrop=new(){Text="Auto crop subject",Checked=true,AutoSize=true},flip=new(){Text="Mirror image projection",AutoSize=true},negative=new(){Text="Front faces negative Z",AutoSize=true} /* actual default comes from Settings.NegativeZFront via Apply() below */,archive=new(){Text="Include a separate BODY.HQR copy",Checked=true,AutoSize=true};
    readonly NumericUpDown flatColours=Number(2,40,14);
    readonly CheckBox pairImage=new(){Text="The picture holds a front view (left) and a back view (right)",AutoSize=true},symmetric=new(){Text="Make the figure symmetric (copy the left half)",AutoSize=true},lit=new(){Text="Game lighting: shade the body like the game's own characters",Checked=true,AutoSize=true};
    readonly Dictionary<int,BodyStyleStats> styleStats=[];
    readonly ModelView preview=new(){Dock=DockStyle.Fill};
    readonly PictureBox reference=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Renderer.ViewBackground};
    readonly Label status=new(){Dock=DockStyle.Bottom,Height=52,Padding=new Padding(16,8,16,8),Text="Choose an image, generate, then inspect the preview before exporting."};
    readonly Button generate=new(){Text="Generate preview",AutoSize=true},export=new(){Text="Export selected games",AutoSize=true,Enabled=false};
    readonly ComboBox previewGame=Combo("LBA1","LBA2");
    readonly List<Generated> generated=[];
    Settings? generatedSettings;
    public MainForm()
    {
        Text="LBA Body Studio — Image to character body";Size=new(1400,930);MinimumSize=new(1050,720);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterScreen;
        var main=new SplitContainer(){Width=1350,Dock=DockStyle.Fill,FixedPanel=FixedPanel.Panel1,SplitterDistance=390,Panel1MinSize=360};Controls.Add(main);Controls.Add(status);
        var fields=new TableLayoutPanel(){Dock=DockStyle.Fill,AutoScroll=true,ColumnCount=1,Padding=new Padding(18),BackColor=Color.FromArgb(243,245,248)};main.Panel1.Controls.Add(fields);
        void Add(Control c){c.Margin=new Padding(0,0,0,10);c.Dock=DockStyle.Top;fields.Controls.Add(c);}
        void Label(string text){Add(new Label(){Text=text,AutoSize=true,Font=new Font(Font,FontStyle.Bold)});}
        void Field(string text,Control c){Add(new Label(){Text=text,AutoSize=true,Margin=new Padding(0,2,0,3)});Add(c);}
        Control Browse(TextBox text,bool file)
        {
            var row=new TableLayoutPanel(){AutoSize=true,ColumnCount=2,Dock=DockStyle.Top};row.ColumnStyles.Add(new(SizeType.Percent,100));row.ColumnStyles.Add(new(SizeType.Absolute,42));text.Dock=DockStyle.Fill;row.Controls.Add(text);
            var button=new Button(){Text="…",Dock=DockStyle.Fill};row.Controls.Add(button);
            button.Click+=(_,_)=>{if(file){using var d=new OpenFileDialog(){Filter="Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif"};if(d.ShowDialog()==DialogResult.OK){text.Text=d.FileName;LoadImage();}}else{using var d=new FolderBrowserDialog(){SelectedPath=text.Text};if(d.ShowDialog()==DialogResult.OK)text.Text=d.SelectedPath;}};
            return row;
        }
        Label("LBA BODY STUDIO");Add(new Label(){Text="Fit a native character template to a reference image. Both games export independently with their own rig and palette.",AutoSize=true,MaximumSize=new Size(325,0)});
        Field("Reference image",Browse(imagePath,true));Field("Image layout",layout);Field("Subject mask",mask);Field("Mask threshold",threshold);Add(autoCrop);Add(flip);Add(negative);
        Label("Flat picture for the game");Add(new Label(){Text="Turn any picture into the flat, few-colour, palette-exact picture the generator works best with (the game's own bodies use a dozen or so colours). Or export a game body as such a picture to see what one looks like: the template's game and index are chosen below.",AutoSize=true,MaximumSize=new Size(325,0)});
        Field("Flat colours",flatColours);Add(pairImage);Add(symmetric);
        var convertButton=new Button(){Text="Convert the reference image to game style",AutoSize=true};Add(convertButton);convertButton.Click+=async(_,_)=>await ConvertStyle();
        var sheetButton=new Button(){Text="Export a flat sheet of the selected template…",AutoSize=true};Add(sheetButton);sheetButton.Click+=(_,_)=>ExportSheet();
        Add(lit);
        Label("Head details — optional");Add(headDetails);Field("Bandana lettering (up to 8 characters)",bandanaText);
        Add(new Label(){Text="New humanoid mode only. Builds actual head-bone geometry. Uses a compact block font; text is supplied here, not read automatically from the image.",AutoSize=true,MaximumSize=new Size(325,0)});
        headDetails.CheckedChanged+=(_,_)=>bandanaText.Enabled=headDetails.Checked;
        Label("Game assets and template");Field("LBA1 installation",Browse(game1,false));Field("LBA1 body index (zero-based)",body1);Field("LBA2 installation",Browse(game2,false));Field("LBA2 body index (zero-based)",body2);
        var originalButton=new Button(){Text="Preview original selected template",AutoSize=true};Add(originalButton);originalButton.Click+=(_,_)=>PreviewOriginal();
        Label("Shape and detail");Field("Mesh generation",method);Field("Silhouette fit (%) — template mode",fit);Field("Height (%)",height);Field("Width (%)",width);Field("Depth (%) — inferred",depth);Field("Head proportion (%) — 70 recommended",head);Field("Refinement primitive budget (0 = no extra detail)",budget);
        Label("Export");Field("Output format",target);Field("Output folder",Browse(output,false));Add(archive);
        var buttons=new FlowLayoutPanel(){Height=50,Dock=DockStyle.Bottom,Padding=new Padding(12,8,0,0)};buttons.Controls.Add(generate);buttons.Controls.Add(export);main.Panel1.Controls.Add(buttons);
        var projectButtons=new FlowLayoutPanel(){AutoSize=true};var save=new Button(){Text="Save settings",AutoSize=true};var load=new Button(){Text="Load settings",AutoSize=true};projectButtons.Controls.Add(save);projectButtons.Controls.Add(load);Add(projectButtons);
        Add(new Label(){Text="Best input: upright full-body views on a plain background. Optional head details replace projected facial colours with stylised geometry and reserve the mesh budget for the face. Check Head close-up, then the whole body. In-game appearance still needs testing.",AutoSize=true,MaximumSize=new Size(325,0)});
        var right=new TableLayoutPanel(){Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};right.RowStyles.Add(new(SizeType.Absolute,48));right.RowStyles.Add(new(SizeType.Percent,100));main.Panel2.Controls.Add(right);
        var toolbar=new FlowLayoutPanel(){Dock=DockStyle.Fill,Padding=new Padding(8)};previewGame.Width=95;toolbar.Controls.Add(previewGame);
        var wire=new CheckBox(){Text="Wireframe",AutoSize=true};var bones=new CheckBox(){Text="Bones",AutoSize=true};var front=new Button(){Text="Front",AutoSize=true};var back=new Button(){Text="Back",AutoSize=true};toolbar.Controls.Add(wire);toolbar.Controls.Add(bones);toolbar.Controls.Add(front);toolbar.Controls.Add(back);right.Controls.Add(toolbar);
        var closeUp=new CheckBox(){Text="Head close-up",AutoSize=true};toolbar.Controls.Add(closeUp);closeUp.CheckedChanged+=(_,_)=>{preview.HeadOnly=closeUp.Checked;preview.Invalidate();};
        var tabs=new TabControl(){Dock=DockStyle.Fill};var modelTab=new TabPage("3D body");modelTab.Controls.Add(preview);var imageTab=new TabPage("Reference image");imageTab.Controls.Add(reference);tabs.TabPages.Add(modelTab);tabs.TabPages.Add(imageTab);right.Controls.Add(tabs);
        wire.CheckedChanged+=(_,_)=>{preview.Wire=wire.Checked;preview.Invalidate();};bones.CheckedChanged+=(_,_)=>{preview.Bones=bones.Checked;preview.Invalidate();};front.Click+=(_,_)=>{preview.Yaw=negative.Checked?0:MathF.PI;preview.Invalidate();};back.Click+=(_,_)=>{preview.Yaw=negative.Checked?MathF.PI:0;preview.Invalidate();};previewGame.SelectedIndexChanged+=(_,_)=>ShowGenerated();
        generate.Click+=async(_,_)=>await Generate();export.Click+=async(_,_)=>await Export();
        save.Click+=(_,_)=>{using var d=new SaveFileDialog(){Filter="Body Studio project|*.json",FileName="body-project.json"};if(d.ShowDialog()==DialogResult.OK)Try(()=>File.WriteAllText(d.FileName,JsonSerializer.Serialize(ReadSettings(),new JsonSerializerOptions{WriteIndented=true})));};
        load.Click+=(_,_)=>{using var d=new OpenFileDialog(){Filter="Body Studio project|*.json"};if(d.ShowDialog()==DialogResult.OK)Try(()=>{Apply(JsonSerializer.Deserialize<Settings>(File.ReadAllText(d.FileName))??throw new InvalidDataException("Empty project."));LoadImage();});};
        Apply(new Settings(){OutputFolder=Path.Combine(AppContext.BaseDirectory,"Body Exports"),Lba1Folder=LBA2LevelEditor.EditorSettings.Current.Lba1Directory,Lba2Folder=LBA2LevelEditor.EditorSettings.Current.GameDirectory});
        // Any setting change invalidates the export snapshot until regenerated.
        foreach(Control c in Descendants(fields))
        {
            if(c is TextBox t)t.TextChanged+=(_,_)=>InvalidateGeneration();
            if(c is NumericUpDown n)n.ValueChanged+=(_,_)=>InvalidateGeneration();
            if(c is ComboBox cb)cb.SelectedIndexChanged+=(_,_)=>InvalidateGeneration();
            if(c is CheckBox ch)ch.CheckedChanged+=(_,_)=>InvalidateGeneration();
        }
    }
    static IEnumerable<Control> Descendants(Control c){foreach(Control child in c.Controls){yield return child;foreach(var d in Descendants(child))yield return d;}}
    void InvalidateGeneration(){generatedSettings=null;export.Enabled=false;}
    static ComboBox Combo(params string[] items){var c=new ComboBox(){DropDownStyle=ComboBoxStyle.DropDownList};c.Items.AddRange(items);c.SelectedIndex=0;return c;}
    static NumericUpDown Number(int min,int max,int value)=>new(){Minimum=min,Maximum=max,Value=value};
    Settings ReadSettings()=>new(){ImagePath=imagePath.Text,Lba1Folder=game1.Text,Lba2Folder=game2.Text,Lba1Body=(int)body1.Value,Lba2Body=(int)body2.Value,Target=target.Text,Layout=layout.Text,Method=method.Text,Mask=mask.Text,Threshold=(int)threshold.Value,AutoCrop=autoCrop.Checked,FlipFront=flip.Checked,NegativeZFront=negative.Checked,Fit=(float)fit.Value/100,Height=(float)height.Value/100,Width=(float)width.Value/100,Depth=(float)depth.Value/100,HeadScale=(float)head.Value/100,DetailBudget=(int)budget.Value,HeadDetails=headDetails.Checked,BandanaText=bandanaText.Text,ArchiveCopy=archive.Checked,OutputFolder=output.Text,Lit=lit.Checked};
    void Apply(Settings s)
    {
        imagePath.Text=s.ImagePath;game1.Text=s.Lba1Folder;game2.Text=s.Lba2Folder;output.Text=s.OutputFolder;
        headDetails.Checked=s.HeadDetails;bandanaText.Text=s.BandanaText;bandanaText.Enabled=s.HeadDetails;
        body1.Value=Math.Clamp(s.Lba1Body,0,10000);body2.Value=Math.Clamp(s.Lba2Body,0,10000);threshold.Value=Math.Clamp(s.Threshold,1,254);fit.Value=Math.Clamp((decimal)s.Fit*100,0,100);height.Value=Math.Clamp((decimal)s.Height*100,25,300);width.Value=Math.Clamp((decimal)s.Width*100,25,300);depth.Value=Math.Clamp((decimal)s.Depth*100,25,300);head.Value=Math.Clamp((decimal)s.HeadScale*100,25,150);budget.Value=Math.Clamp(s.DetailBudget,0,540);
        lit.Checked=s.Lit;target.SelectedItem=s.Target;layout.SelectedItem=s.Layout;method.SelectedItem=s.Method;mask.SelectedItem=s.Mask;autoCrop.Checked=s.AutoCrop;flip.Checked=s.FlipFront;negative.Checked=s.NegativeZFront;archive.Checked=s.ArchiveCopy;
    }
    void LoadImage()=>Try(()=>{using var b=new Bitmap(imagePath.Text);var copy=new Bitmap(b);reference.Image?.Dispose();reference.Image=copy;});
    void ShowGenerated(){preview.Model=generated.FirstOrDefault(g=>g.Body.Game==previewGame.SelectedIndex+1);preview.Invalidate();}
    void PreviewOriginal()=>Try(()=>{int game=previewGame.SelectedIndex+1;var s=ReadSettings();string folder=game==1?s.Lba1Folder:s.Lba2Folder;int index=game==1?s.Lba1Body:s.Lba2Body;string path=Generator.BodyArchive(folder);preview.Model=new(Body.Read(new Hqr(path).Read(index),game),Generator.Palette(folder),index,path,"");preview.Invalidate();status.Text="Template shown from "+Path.GetFileName(path)+". Generate to return to your image-derived body.";});
    async Task Generate()
    {
        var s=ReadSettings();Enabled=false;status.Text="Fitting geometry, projecting colours, and checking native bodies…";
        try
        {
            var models=await Task.Run(()=>{var list=new List<Generated>();if(s.Target is "Both" or "LBA1")list.Add(Generator.Generate(s,1));if(s.Target is "Both" or "LBA2")list.Add(Generator.Generate(s,2));return list;});
            generated.Clear();generated.AddRange(models);generatedSettings=s;previewGame.SelectedIndex=models[0].Body.Game-1;ShowGenerated();export.Enabled=true;LoadImage();status.Text="Preview ready. Drag to rotate; check front, back and bones. Export uses this exact generated snapshot.";
        }
        catch(Exception e){generatedSettings=null;export.Enabled=false;Error(e);}
        finally{Enabled=true;}
    }
    async Task Export()
    {
        if(generatedSettings==null)return;Enabled=false;status.Text="Writing body files, archive copies and previews…";
        try{string folder=await Task.Run(()=>Generator.Export(generatedSettings,generated));status.Text="Exported to "+folder;MessageBox.Show(this,"Export complete. Original game files were not modified.\n\n"+folder+"\n\nRead INSTALL.txt before testing in a copy of the game.","Export complete");}
        catch(Exception e){Error(e);}finally{Enabled=true;}
    }
    // ---- flat pictures ----
    static byte[] PaletteBytes(string folder)=>new Hqr(Path.Combine(folder,"RESS.HQR")).Read(0);
    BodyStyleStats StatsFor(int game,string folder)
    {
        if(styleStats.TryGetValue(game,out var known))return known;
        var hqr=new Hqr(Generator.BodyArchive(folder));var bodies=new List<byte[]>();
        for(int i=0;i<hqr.Count;i++){try{bodies.Add(hqr.Read(i));}catch(InvalidDataException){}}
        return styleStats[game]=BodyStyleStats.Analyse(game,bodies);
    }
    // The template's game and body index are the preview game's own fields (LBA1 / LBA2 installation and body index).
    void ExportSheet()=>Try(()=>
    {
        int game=previewGame.SelectedIndex+1;var s=ReadSettings();string folder=game==1?s.Lba1Folder:s.Lba2Folder;int index=game==1?s.Lba1Body:s.Lba2Body;
        var body=Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(index),game);
        var sheet=FlatSheet.Render(body,PaletteBytes(folder));
        using var d=new SaveFileDialog(){Filter="PNG image|*.png",FileName=$"lba{game}-body{index}-flat-sheet.png"};
        if(d.ShowDialog()!=DialogResult.OK)return;
        FlatBitmap.Save(sheet,d.FileName);
        status.Text=$"Saved the game's own body {index} of LBA{game} as a flat front + back sheet ({FlatSheet.Colours(body).Length} colours, {body.Faces.Count} polygons): {d.FileName}. It is the kind of picture the generator reads.";
    });
    async Task ConvertStyle()
    {
        var s=ReadSettings();int game=previewGame.SelectedIndex+1;string folder=game==1?s.Lba1Folder:s.Lba2Folder;
        if(!File.Exists(s.ImagePath)){Error(new InvalidDataException("Choose a reference image first."));return;}
        var options=new StyleOptions(){Colours=(int)flatColours.Value,Symmetrise=symmetric.Checked,BackFromFront=true};
        bool pair=pairImage.Checked;Enabled=false;status.Text="Reading the game's bodies and flattening the picture…";
        try
        {
            var (result,path)=await Task.Run(()=>
            {
                options.Allowed=StatsFor(game,folder).RecommendedDisplay(3);
                var image=FlatBitmap.Load(s.ImagePath);var palette=PaletteBytes(folder);
                var r=pair?GameStyle.ConvertPair(image,palette,options):GameStyle.Convert(image,palette,options);
                string directory=Path.Combine(string.IsNullOrWhiteSpace(s.OutputFolder)?AppContext.BaseDirectory:s.OutputFolder,"Flat sheets");Directory.CreateDirectory(directory);
                string file=Path.Combine(directory,Path.GetFileNameWithoutExtension(s.ImagePath)+$"-lba{game}-flat.png");FlatBitmap.Save(r.Sheet,file);
                return (r,file);
            });
            imagePath.Text=path;layout.SelectedItem="Front + back";mask.SelectedItem="Transparent background";LoadImage();
            status.Text=$"Flat sheet saved to {path} and set as the reference image (front + back, transparent background): {result.Notes}. Generate to build the body.";
        }
        catch(Exception e){Error(e);}
        finally{Enabled=true;}
    }
    void Try(Action action){try{action();}catch(Exception e){Error(e);}}
    void Error(Exception e){status.Text=e.Message;MessageBox.Show(this,e.Message,"Body Studio",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    protected override void Dispose(bool disposing){if(disposing)reference.Image?.Dispose();base.Dispose(disposing);}
}
