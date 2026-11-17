using System;
using System.Drawing;
using NekoBridge.Windows.Input;

var screen=new Rectangle(0,0,2560,1440);
void Check(Rectangle? window,Point expected,string name)
{
    var actual=CursorParking.SelectParkingPoint(screen,window);
    if(actual!=expected)throw new Exception(name+": "+actual);
    Console.WriteLine("PASS "+name);
}
Check(null,new Point(1280,4),"default top");
Check(new Rectangle(0,0,2560,1390),new Point(1280,1435),"Start host geometric departure");
Check(screen,new Point(1280,4),"full-screen no available outside point");
Check(new Rectangle(0,100,500,600),new Point(1280,4),"ordinary window unchanged");
var negative=new Rectangle(-2560,-1440,2560,1440);
var point=CursorParking.SelectParkingPoint(negative,new Rectangle(-2560,-1440,2560,1390));
if(point!=new Point(-1280,-5))throw new Exception("negative monitor coordinates");
Console.WriteLine("PASS negative monitor coordinates; no input injected");
