using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class WikiFaviconCutout {
 public static void Run(string source,string folder) {
  using(var input=new Bitmap(source)) using(var cut=new Bitmap(input.Width,input.Height,PixelFormat.Format32bppArgb)) {
   int width=input.Width,height=input.Height;
   var mask=new bool[width*height];
   int removed=0;
   bool[] background=new bool[width*height];
   for(int y=0;y<height;y++)for(int x=0;x<width;x++) {
    Color p=input.GetPixel(x,y);
    background[y*width+x]=Math.Abs(p.R-32)<=2&&Math.Abs(p.G-32)<=2&&Math.Abs(p.B-32)<=2;
   }
   for(int y=0;y<height;y++)for(int x=0;x<width;x++) {
    if(background[y*width+x]){removed++;continue;}
    Color p=input.GetPixel(x,y);bool edge=false;
    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++){int nx=x+dx,ny=y+dy;if(nx>=0&&ny>=0&&nx<width&&ny<height&&background[ny*width+nx])edge=true;}
    double alpha=edge?Math.Max(0,Math.Min(1,(32.0-p.R)/32.0)):1;
    int a=(int)Math.Round(255*alpha);
    if(a==0){removed++;continue;}
    int r=(int)Math.Round((p.R-32*(1-alpha))/alpha),g=(int)Math.Round((p.G-32*(1-alpha))/alpha),b=(int)Math.Round((p.B-32*(1-alpha))/alpha);
    cut.SetPixel(x,y,Color.FromArgb(a,Math.Max(0,Math.Min(255,r)),Math.Max(0,Math.Min(255,g)),Math.Max(0,Math.Min(255,b))));
    mask[y*width+x]=a>40;
   }
   // Find the emblem's main component to discard the clipped stray bottom mark.
   bool[] visited=new bool[mask.Length];
   int bestArea=0,bestCount=0,minX=0,minY=0,maxX=0,maxY=0;
   for(int n=0;n<mask.Length;n++) {
    if(!mask[n]||visited[n])continue;
    int[] q=new int[mask.Length];int qHead=0,qTail=0;q[qTail++]=n;visited[n]=true;
    int count=0,l=width,t=height,r=0,bottom=0;
    while(qHead<qTail){int i=q[qHead++],x=i%width,y=i/width;count++;l=Math.Min(l,x);r=Math.Max(r,x);t=Math.Min(t,y);bottom=Math.Max(bottom,y);
     for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++){int nx=x+dx,ny=y+dy;if(nx<0||ny<0||nx>=width||ny>=height)continue;int j=ny*width+nx;if(mask[j]&&!visited[j]){visited[j]=true;q[qTail++]=j;}}
    }
    int area=(r-l+1)*(bottom-t+1);if(area>bestArea){bestArea=area;bestCount=count;minX=l;maxX=r;minY=t;maxY=bottom;}
   }
   var bounds=new Rectangle(Math.Max(0,minX-2),Math.Max(0,minY-2),Math.Min(width,maxX+3)-Math.Max(0,minX-2),Math.Min(height,maxY+3)-Math.Max(0,minY-2));
   int side=Math.Max(bounds.Width,bounds.Height)+16;
   using(var square=new Bitmap(side,side,PixelFormat.Format32bppArgb)) {
    using(var g=Graphics.FromImage(square)){g.CompositingMode=CompositingMode.SourceCopy;g.DrawImage(cut,new Rectangle((side-bounds.Width)/2,(side-bounds.Height)/2,bounds.Width,bounds.Height),bounds,GraphicsUnit.Pixel);}
    square.Save(System.IO.Path.Combine(folder,"deeplagoon-transparent.png"),ImageFormat.Png);
    foreach(int size in new[]{16,32,48,64,180,192,256,512}) using(var icon=new Bitmap(size,size,PixelFormat.Format32bppArgb)) {
     using(var g=Graphics.FromImage(icon)){g.CompositingMode=CompositingMode.SourceCopy;g.CompositingQuality=CompositingQuality.HighQuality;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.DrawImage(square,new Rectangle(0,0,size,size),new Rectangle(0,0,side,side),GraphicsUnit.Pixel);}
     icon.Save(System.IO.Path.Combine(folder,"favicon-"+size+".png"),ImageFormat.Png);
    }
    Console.WriteLine("Transparent background pixels: "+removed+"; main component: "+bestCount+"; square: "+side);
   }
  }
 }
}



