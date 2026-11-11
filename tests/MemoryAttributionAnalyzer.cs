using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

// Diagnostic only. Never load into the product; never emit string contents.
public static class MemoryAttributionAnalyzer
{
    public sealed class Count { public string Type; public long CountAll,BytesAll,CountRooted,BytesRooted; }
    public sealed class Range { public ulong Start,End; public string Label; public Range(ulong start,ulong end,string label){Start=start;End=end;Label=label;} }
    public sealed class PageGroup { public string Label; public long Bytes; }
    public sealed class MapResult { public long PrivateResident,SharedResident,PrivateCommit,WorkingSet; public int PageSize; public PageGroup[] Groups; public long ClassifiedTotal; internal Dictionary<ulong,string> Pages; }
    public sealed class ObjectRow { public string Address,Type,RootPath; public ulong Bytes; }
    public sealed class SegmentRow { public string Kind; public ulong Committed,Reserved,ObjectRange; }
    public sealed class ThreadRow { public uint OsId; public int ManagedId; public string State; public string[] Frames; }
    public sealed class Result {
        public string Status="starting",Error,Library,ClrVersion; public int Pid,RootCount,InvalidObjects,InvalidRoots,MissingReferences,IgnoredNonHeapRootCandidates;
        public long CaptureMs,ElapsedMs,RootedBytes,UnrootedBytes,FreeBytes,Objects,ReachableObjects;
        public bool HeapWalkable,ResourcesReleased,SameIdentityAfter,NoDump=true; public long GcCommittedBytes;
        public Count[] Types; public ObjectRow[] LargestRooted; public SegmentRow[] Segments;
        public string[] InvalidRootDetails;
        public PageGroup[] ClrNativeRanges; public MapResult LiveMemoryMap;
        public ObjectRow[] KnownStaticOwners;
        public ThreadRow[] Threads;
    }
    private static void Deadline(Stopwatch watch){if(watch.ElapsedMilliseconds>22000)throw new TimeoutException("Diagnostic analysis budget exceeded");}
    public static Result Analyze(int pid,long startTicks,string expectedPath,string allocationFile,bool injectTimeout)
    {
        var result=new Result{Pid=pid,Library=typeof(DataTarget).Assembly.FullName};
        var watch=Stopwatch.StartNew(); DataTarget target=null; ClrRuntime runtime=null;
        using(var process=Process.GetProcessById(pid)) {
            if(!String.Equals(process.MainModule.FileName,expectedPath,StringComparison.OrdinalIgnoreCase) || process.StartTime.ToUniversalTime().Ticks!=startTicks)
                throw new InvalidOperationException("PID/start-time/path identity mismatch");
            string name=Path.GetFileName(expectedPath);
            if(name!="ScreenOffMemoryIsolation.exe" && name!="MemoryAttributionFixture.exe")throw new InvalidOperationException("Isolated target names only");
            try {
                // PSS makes original pages shared with its clone. Measure residency
                // BEFORE creating that clone; never interpret the clone-induced drop as a saving.
                result.LiveMemoryMap=ReadMap(process,new List<Range>(),watch);
                var capture=Stopwatch.StartNew(); target=DataTarget.CreateSnapshotAndAttach(pid,null);result.CaptureMs=capture.ElapsedMilliseconds;
                if(injectTimeout)throw new TimeoutException("Injected after-capture timeout; Dispose must run");
                if(target.ClrVersions.Length!=1)throw new InvalidOperationException("Expected one CLR");
                result.ClrVersion=target.ClrVersions[0].Version.ToString();
                runtime=target.ClrVersions[0].CreateRuntime(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscordacwks.dll");
                var heap=runtime.Heap;result.HeapWalkable=heap.CanWalkHeap;
                if(!heap.CanWalkHeap)throw new InvalidOperationException("Snapshot captured during nonwalkable GC state; no retry");
                result.Segments=heap.Segments.Select(s=>new SegmentRow{Kind=s.Kind.ToString(),Committed=s.CommittedMemory.Length,Reserved=s.ReservedMemory.Length,ObjectRange=s.ObjectRange.Length}).ToArray();
                result.GcCommittedBytes=result.Segments.Sum(s=>(long)s.Committed);
                var ranges=new List<Range>();
                foreach(var segment in heap.Segments)ranges.Add(new Range(segment.CommittedMemory.Start,segment.CommittedMemory.End,"CLR managed GC heap (objects + free space)"));
                foreach(var thread in runtime.Threads)if(thread.StackBase>thread.StackLimit && thread.StackLimit!=0)ranges.Add(new Range(thread.StackLimit,thread.StackBase,"Managed thread stacks"));
                var native=runtime.EnumerateClrNativeHeaps().ToArray();
                result.ClrNativeRanges=native.GroupBy(n=>n.Kind.ToString()+"/"+n.State).Select(g=>new PageGroup{Label=g.Key,Bytes=g.Sum(n=>(long)n.MemoryRange.Length)}).ToArray();
                foreach(var n in native)if(n.State.ToString().IndexOf("Reserve",StringComparison.OrdinalIgnoreCase)<0)ranges.Add(new Range(n.MemoryRange.Start,n.MemoryRange.End,"CLR native / "+n.Kind));
                // Optional exact allocator payload boundaries from the test itself.
                if(File.Exists(allocationFile)) {
                    var allocations=System.Text.Json.JsonDocument.Parse(File.ReadAllText(allocationFile));
                    using(allocations)foreach(var item in allocations.RootElement.EnumerateArray()){
                        ulong address=Convert.ToUInt64(item.GetProperty("address").GetString(),16);
                        ranges.Insert(0,new Range(address,checked(address+item.GetProperty("bytes").GetUInt64()),item.GetProperty("role").GetString()));
                    }
                }
                ClassifyMap(result.LiveMemoryMap,ranges);Deadline(watch);
                var objects=new Dictionary<ulong,ClrObject>();var stats=new Dictionary<string,Count>();
                foreach(var obj in heap.EnumerateObjects()) {
                    Deadline(watch); if(!obj.IsValid){result.InvalidObjects++;continue;}
                    if(obj.IsFree){result.FreeBytes+=(long)obj.Size;continue;}
                    objects[obj.Address]=obj;
                    string type=obj.Type.Name??"<unknown>";Count row;
                    if(!stats.TryGetValue(type,out row))stats[type]=row=new Count{Type=type};
                    row.CountAll++;row.BytesAll+=(long)obj.Size;
                    if(objects.Count>500000)throw new InvalidOperationException("Bounded object count exceeded");
                }
                var seen=new HashSet<ulong>();var queue=new Queue<ulong>();var parent=new Dictionary<ulong,ulong>();var edge=new Dictionary<ulong,string>();
                var addresses=objects.Keys.OrderBy(a=>a).ToArray();var invalidRoots=new List<string>();
                foreach(var root in heap.EnumerateRoots()) {
                    Deadline(watch);result.RootCount++;
                    if(root.Object.IsNull)continue;
                    ulong rootAddress=root.Object.Address;
                    if(!objects.ContainsKey(rootAddress) && root.IsInterior) {
                        int index=Array.BinarySearch(addresses,rootAddress); if(index<0)index=~index-1;
                        if(index>=0 && rootAddress<addresses[index]+objects[addresses[index]].Size)rootAddress=addresses[index];
                    }
                    if(!objects.ContainsKey(rootAddress)){
                        bool inHeap=heap.Segments.Any(s=>rootAddress>=s.Start && rootAddress<s.End);
                        // An invalid, outside-GC address cannot retain any GC object.
                        // Do not ignore valid external objects or ambiguous in-heap candidates.
                        if(!root.Object.IsValid && !inHeap)result.IgnoredNonHeapRootCandidates++;
                        else result.InvalidRoots++;
                        invalidRoots.Add(root.RootKind+" interior="+root.IsInterior+" valid="+root.Object.IsValid+" type="+root.Object.Type?.Name+" inSegment="+inHeap+" address="+root.Object.Address.ToString("X"));continue;
                    }
                    if(seen.Add(rootAddress)){queue.Enqueue(rootAddress);edge[rootAddress]="root:"+root.RootKind;}
                }
                result.InvalidRootDetails=invalidRoots.ToArray();
                while(queue.Count!=0) {
                    Deadline(watch);ulong address=queue.Dequeue();var obj=objects[address];
                    // Dependent handles add edges only from their source, never unconditional roots.
                    foreach(var reference in obj.EnumerateReferencesWithFields(true,true)) {
                        var child=reference.Object;if(child.IsNull)continue;
                        if(!objects.ContainsKey(child.Address)){result.MissingReferences++;continue;}
                        if(seen.Add(child.Address)){
                            parent[child.Address]=address;
                            edge[child.Address]=reference.IsDependentHandle?"dependent-handle":reference.IsField?(reference.Field?.Name??"field"):"element/ref";
                            queue.Enqueue(child.Address);
                        }
                    }
                }
                foreach(var entry in objects) {
                    var obj=entry.Value;result.Objects++;
                    if(seen.Contains(entry.Key)) {var row=stats[obj.Type.Name??"<unknown>"];row.CountRooted++;row.BytesRooted+=(long)obj.Size;result.ReachableObjects++;result.RootedBytes+=(long)obj.Size;}
                    else result.UnrootedBytes+=(long)obj.Size;
                }
                result.Types=stats.Values.OrderByDescending(s=>s.BytesRooted).ThenBy(s=>s.Type,StringComparer.Ordinal).ToArray();
                result.LargestRooted=seen.Select(a=>objects[a]).OrderByDescending(o=>o.Size).Take(20).Select(o=>new ObjectRow{Address=o.Address.ToString("X"),Type=o.Type.Name,Bytes=o.Size,RootPath=RootPath(o.Address,objects,parent,edge)}).ToArray();
                var owners=new List<ObjectRow>();
                foreach(var module in runtime.EnumerateModules()) {
                    Deadline(watch);
                    var type=module.GetTypeByName("System.Diagnostics.NtProcessInfoHelper");if(type==null)continue;
                    foreach(var field in type.StaticFields.Where(f=>f.Name=="CachedBuffer"))foreach(var domain in runtime.AppDomains) {
                        try {var value=field.ReadObject(domain);if(value.IsValid)owners.Add(new ObjectRow{Address=value.Address.ToString("X"),Type=value.Type.Name,Bytes=value.Size,RootPath=type.Name+"."+field.Name});}
                        catch(InvalidOperationException) { /* Optional owner lookup unavailable in this domain; graph stays authoritative. */ }
                    }
                }
                result.KnownStaticOwners=owners.ToArray();
                result.Threads=runtime.Threads.Take(24).Select(t=>new ThreadRow{OsId=t.OSThreadId,ManagedId=t.ManagedThreadId,State=t.State.ToString(),Frames=t.EnumerateStackTrace().Take(18).Select(f=>f.Method?.Signature??f.Kind.ToString()).ToArray()}).ToArray();
                Deadline(watch);
                result.Status=(result.InvalidObjects==0 && result.InvalidRoots==0 && result.MissingReferences==0)?"complete":"partial";
            }catch(Exception error){result.Status="unavailable-or-partial";result.Error=error.GetType().Name+": "+error.Message;}
            finally {
                try{if(runtime!=null)runtime.Dispose();}finally{if(target!=null)target.Dispose();}
                process.Refresh();result.SameIdentityAfter=!process.HasExited && process.StartTime.ToUniversalTime().Ticks==startTicks;
                result.ResourcesReleased=true;result.ElapsedMs=watch.ElapsedMilliseconds;
            }
        }
        return result;
    }
    private static string RootPath(ulong address,Dictionary<ulong,ClrObject> objects,Dictionary<ulong,ulong> parent,Dictionary<ulong,string> edge) {
        var path=new List<string>();int steps=0;
        while(true){path.Add((objects[address].Type.Name??"unknown")+" ["+edge[address]+"]");ulong next;if(!parent.TryGetValue(address,out next))break;address=next;if(++steps>=48){path.Add("<depth limit>");break;}}
        path.Reverse();return String.Join(" -> ",path);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Region {public IntPtr Base,AllocationBase;public uint AllocationProtect;public ushort Partition;public UIntPtr Size;public uint State,Protect,Type;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern UIntPtr VirtualQueryEx(IntPtr process,IntPtr address,out Region region,UIntPtr length);
    [DllImport("psapi.dll",SetLastError=true)] private static extern bool QueryWorkingSet(IntPtr process,IntPtr buffer,int length);
    private static MapResult ReadMap(Process process,List<Range> ranges,Stopwatch watch) {
        var modules=new Dictionary<ulong,string>();foreach(ProcessModule module in process.Modules)modules[(ulong)module.BaseAddress.ToInt64()]=module.ModuleName;
        var groups=new Dictionary<string,long>();var pageLabels=new Dictionary<ulong,string>();long shared=0;int page=Environment.SystemPageSize;
        IntPtr buffer=Marshal.AllocHGlobal(4*1024*1024);
        try {
            if(!QueryWorkingSet(process.Handle,buffer,4*1024*1024))throw new System.ComponentModel.Win32Exception();
            long count=Marshal.ReadIntPtr(buffer).ToInt64();if(count<0 || count>(4*1024*1024-IntPtr.Size)/IntPtr.Size)throw new Exception("Working set buffer bounds");
            Region region=new Region();ulong regionEnd=0;
            var pages=new List<ulong>();for(int i=0;i<count;i++){ulong block=(ulong)Marshal.ReadIntPtr(buffer,(i+1)*IntPtr.Size).ToInt64();if((block&0x100)!=0){shared+=page;continue;}pages.Add(block&~((ulong)page-1));}pages.Sort();
            foreach(ulong address in pages) {
                Deadline(watch);string label=null;
                if(address>=regionEnd){if(VirtualQueryEx(process.Handle,new IntPtr((long)address),out region,new UIntPtr((uint)Marshal.SizeOf<Region>()))==UIntPtr.Zero)throw new System.ComponentModel.Win32Exception();regionEnd=(ulong)region.Base.ToInt64()+region.Size.ToUInt64();}
                foreach(var range in ranges)if(address>=range.Start && address+(ulong)page<=range.End){label=range.Label;break;}
                if(label==null && region.Type==0x1000000){string module;if(!modules.TryGetValue((ulong)region.AllocationBase.ToInt64(),out module))module="unresolved image";label="Image private pages / "+module;}
                if(label==null)label=(region.Type==0x20000?"Unattributed native/private allocation / ":"Unattributed mapped/private pages / ")+((ulong)region.AllocationBase.ToInt64()).ToString("X");
                pageLabels.Add(address,label);
                long bytes;groups.TryGetValue(label,out bytes);groups[label]=bytes+page;
            }
            process.Refresh();var result=new MapResult{PageSize=page,PrivateResident=(long)pages.Count*page,SharedResident=shared,PrivateCommit=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64,Groups=groups.Select(g=>new PageGroup{Label=g.Key,Bytes=g.Value}).OrderByDescending(g=>g.Bytes).ToArray()};
            result.Pages=pageLabels;result.ClassifiedTotal=result.Groups.Sum(g=>g.Bytes);if(result.ClassifiedTotal!=result.PrivateResident)throw new Exception("Page accounting did not reconcile");return result;
        }finally{Marshal.FreeHGlobal(buffer);}
    }
    private static void ClassifyMap(MapResult map,List<Range> ranges) {
        var groups=new Dictionary<string,long>();
        foreach(var item in map.Pages) {
            string label=item.Value;foreach(var range in ranges)if(item.Key>=range.Start && item.Key+(ulong)map.PageSize<=range.End){label=range.Label;break;}
            long bytes;groups.TryGetValue(label,out bytes);groups[label]=bytes+map.PageSize;
        }
        map.Groups=groups.Select(g=>new PageGroup{Label=g.Key,Bytes=g.Value}).OrderByDescending(g=>g.Bytes).ToArray();
        map.ClassifiedTotal=map.Groups.Sum(g=>g.Bytes);if(map.ClassifiedTotal!=map.PrivateResident)throw new Exception("Classified page accounting mismatch");
    }
}
