using System.IO;
using System.Linq;
using System.Numerics;

namespace LbaBodyStudio;

// A new rigid-skinned mesh, generated from image cross-sections. The known Twinsen
// hierarchy is checked before using its animation semantics; other rigs use template fitting.
public static class Humanoid
{
    public static Body Build(Body donor,ReferenceImage image,Settings settings)
    {
        int[] parents=[-1,0,1,2,3,4,3,6,2,8,9,2,11,12,3,14,15,3,17];
        if(donor.Bones.Count!=19||!donor.Bones.Select(b=>b.Parent).SequenceEqual(parents))
            throw new InvalidDataException("New humanoid requires the standard 19-bone Twinsen rig (body index 0). Choose index 0 or select Fit template for other rigs.");
        float h=donor.World().Max(v=>v.Y),w=h*image.Crop.Width/image.Crop.Height;

        // Preserve the original rest skeleton, not only its hierarchy.
        var donorWorld=donor.World();
        Vector3[] origins=donor.Bones.Select(b=>b.Parent<0?Vector3.Zero:donorWorld[b.Pivot]).ToArray();
        var vertices=Enumerable.Range(0,19).Select(_=>new List<Vector3>()).ToArray();
        var faces=new List<(int Bone,int[] Points,int Tone)>();
        // Parent-owned anchors are the exact pivot points referenced by each child.
        var pivots=new int[19];
        vertices[0].Add(Vector3.Zero);
        for(int i=1;i<19;i++){pivots[i]=vertices[parents[i]].Count;vertices[parents[i]].Add(origins[i]);}
        (float left,float right) Span(float y,string part,int side=0)
        {
            int row=Math.Clamp((int)((1-y)*255),0,255);var runs=image.Runs[row];
            float left,right;
            if(part=="head")
            {
                var span=image.Rows[row];left=span.Left-.5f;right=span.Right-.5f;
                if(right-left<.05f){left=-.13f;right=.13f;}
                left=Math.Max(left,-.20f);right=Math.Min(right,.20f);
            }
            else if(part=="torso")
            {
                var central=runs.Where(r=>r.Left<.55f&&r.Right>.45f).OrderByDescending(r=>r.Right-r.Left).FirstOrDefault();
                left=central==default?-.23f:central.Left-.5f;right=central==default?.23f:central.Right-.5f;
                left=Math.Max(left,-.32f);right=Math.Min(right,.32f);
            }
            else
            {
                if(part is "leg" or "foot")
                {
                    float desired=.5f+side*.22f;
                    var covering=runs.Where(r=>r.Left<=desired&&r.Right>=desired).FirstOrDefault();
                    if(covering!=default)
                    {
                        left=covering.Left-.5f;right=covering.Right-.5f;
                        if(side>0)left=Math.Max(left,.025f);else right=Math.Min(right,-.025f);
                        return(left,right);
                    }
                }
                var candidates=runs.Where(r=>side>0?(r.Left+r.Right)/2>.54f:(r.Left+r.Right)/2<.46f).ToArray();
                if(candidates.Length>0)
                {
                    var run=part=="arm"?(side>0?candidates.MaxBy(r=>r.Right):candidates.MinBy(r=>r.Left)):(side>0?candidates.MinBy(r=>r.Left):candidates.MaxBy(r=>r.Right));
                    left=run.Left-.5f;right=run.Right-.5f;
                    if(part=="arm"&&right-left>.23f){float centre=side*.35f;left=centre-.075f;right=centre+.075f;}
                }
                else {float centre=side*(part=="arm"?.38f:.22f),radius=part=="arm"?.075f:.115f;left=centre-radius;right=centre+radius;}
            }
            return(left,right);
        }
        void Loft(int bone,float[] rows,string part,int side=0,int segments=6)
        {
            var list=vertices[bone];int begin=list.Count;
            for(int r=0;r<rows.Length;r++)
            {
                float y=rows[r];var span=Span(y,part,side);float centre=(span.left+span.right)/2,radius=(span.right-span.left)/2;
                if(part=="head")radius*=settings.HeadScale/.7f;
                float depth=part switch{"head"=>radius*w*.90f,"torso"=>Math.Min(radius*w*.6f,h*.072f),"foot"=>h*.072f,_=>radius*w*.92f};
                if(part=="head"&&r==0){radius*=.35f;depth*=.35f;}
                float centreZ=part=="foot"?-h*.023f:0;
                if(settings.HeadDetails&&part=="head")
                {
                    centre=0;
                    float shape=y>.995f?.25f:y>.97f?.88f:y>.91f?1:y>.86f?.83f:.53f;
                    radius=h*.055f/w*shape*(settings.HeadScale/.7f);depth=radius*w*.9f;
                }
                for(int j=0;j<segments;j++)
                {
                    float angle=j*2*MathF.PI/segments;
                    list.Add(new((centre+radius*MathF.Cos(angle))*w,y*h,centreZ+depth*MathF.Sin(angle)));
                }
            }
            int tone=settings.HeadDetails&&part=="head"?0:-1;
            for(int r=0;r<rows.Length-1;r++)for(int j=0;j<segments;j++)faces.Add((bone,[begin+r*segments+j,begin+r*segments+(j+1)%segments,begin+(r+1)*segments+(j+1)%segments,begin+(r+1)*segments+j],tone));
            // Convex planar caps tiled with quads, reducing classic engine primitive usage.
            for(int j=1;j<segments-2;j+=2)
            {
                faces.Add((bone,[begin,begin+j+2,begin+j+1,begin+j],tone));
                int b=begin+(rows.Length-1)*segments;faces.Add((bone,[b,b+j,b+j+1,b+j+2],tone));
            }
        }
        if(settings.HeadDetails)
        {
            // Reserve vertex capacity for actual facial geometry instead of subdividing clothing.
            Loft(2,[.56f,.47f],"torso");Loft(3,[.845f,.815f,.76f,.56f],"torso");
            Loft(4,[.81f,.65f],"arm",1);Loft(5,[.65f,.46f,.435f],"arm",1);
            Loft(6,[.81f,.65f],"arm",-1);Loft(7,[.65f,.46f,.435f],"arm",-1);
            Loft(8,[.51f,.29f],"leg",1);Loft(9,[.29f,.07f],"leg",1);Loft(10,[.07f,.003f],"foot",1);
            Loft(11,[.51f,.29f],"leg",-1);Loft(12,[.29f,.07f],"leg",-1);Loft(13,[.07f,.003f],"foot",-1);
            Loft(14,[1f,.98f,.966f,.927f,.875f,.845f],"head",0,12);
            HeadDecoration.Add(vertices[14],(ids,tone)=>faces.Add((14,ids,tone)),h,settings);
        }
        else
        {
        Loft(2,[.56f,.51f,.47f],"torso");
        Loft(3,[.845f,.815f,.76f,.66f,.56f],"torso");
        Loft(4,[.81f,.74f,.65f],"arm",1);Loft(5,[.65f,.55f,.46f,.435f],"arm",1);
        Loft(6,[.81f,.74f,.65f],"arm",-1);Loft(7,[.65f,.55f,.46f,.435f],"arm",-1);
        Loft(8,[.51f,.39f,.29f],"leg",1);Loft(9,[.29f,.20f,.07f],"leg",1);Loft(10,[.07f,.025f,.003f],"foot",1);
        Loft(11,[.51f,.39f,.29f],"leg",-1);Loft(12,[.29f,.20f,.07f],"leg",-1);Loft(13,[.07f,.025f,.003f],"foot",-1);
        Loft(14,[1f,.985f,.969f,.958f,.945f,.929f,.917f,.905f,.89f,.872f,.845f],"head",0,8);
        }
        // Empty accessory bones still have a point so all engine animation groups are valid.
        for(int i=0;i<19;i++)if(vertices[i].Count==0)vertices[i].Add(origins[i]);
        var body=new Body(){Game=donor.Game,Header=(byte[])donor.Header.Clone()};int[] starts=new int[19];
        for(int i=0;i<19;i++)
        {
            starts[i]=body.Vertices.Count;body.Vertices.AddRange(vertices[i]);int pivot=i==0?0:starts[parents[i]]+pivots[i];byte[] record=new byte[donor.Game==1?38:8];
            if(donor.Game==1){BitConverter.GetBytes((ushort)(starts[i]*6)).CopyTo(record,0);BitConverter.GetBytes((ushort)vertices[i].Count).CopyTo(record,2);BitConverter.GetBytes((ushort)(pivot*6)).CopyTo(record,4);BitConverter.GetBytes((short)(parents[i]<0?-1:parents[i]*38)).CopyTo(record,6);}
            body.Bones.Add(new(starts[i],vertices[i].Count,pivot,parents[i],record));
        }
        foreach(var f in faces)body.Faces.Add(new(f.Points.Select(p=>p+starts[f.Bone]).ToArray(),0,f.Tone));
        if(settings.HeadDetails&&body.Vertices.Count>body.Limit)
            throw new InvalidDataException($"The bandana label needs too many points for LBA{body.Game} ({body.Vertices.Count}/{body.Limit}). Shorten the text or choose letters with simpler shapes.");
        body.SetWorld(body.Vertices.ToArray());body.Validate();return body;
    }
}


