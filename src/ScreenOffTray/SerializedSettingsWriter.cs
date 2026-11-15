using System;
using System.Collections.Generic;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenOffTray
{
    // Transactions execute serially off the dispatch thread. A pending disk call
    // is never replaced, nor declared cancelled after it may have written.
    internal sealed class SerializedSettingsWriter
    {
        internal static readonly SerializedSettingsWriter Default = new SerializedSettingsWriter();
        private readonly object sync = new object();
        private readonly Queue<Action> work = new Queue<Action>();
        private bool running, sealedWriter;
        private Task sealedCompletion;
        private readonly Func<Dictionary<string,object>> read;
        private readonly Action<Dictionary<string,object>> restore;
        private readonly Action save;
        internal SerializedSettingsWriter() : this(delegate {
            var result=new Dictionary<string,object>();var settings=Properties.Settings.Default;
            foreach(SettingsProperty property in settings.Properties)result[property.Name]=settings[property.Name];return result;
        },delegate(Dictionary<string,object> values) {foreach(var pair in values)Properties.Settings.Default[pair.Key]=pair.Value;},delegate {Properties.Settings.Default.Save();}) {}
        internal SerializedSettingsWriter(Func<Dictionary<string,object>> read,Action<Dictionary<string,object>> restore,Action save)
        {this.read=read;this.restore=restore;this.save=save;}
        internal Task UpdateAsync(Action change)
        {
            return Enqueue(delegate {
                var before=read();
                try {
                    change();bool changed=false;var after=read();
                    foreach(var pair in before) if(!after.ContainsKey(pair.Key) || !Object.Equals(pair.Value,after[pair.Key])){changed=true;break;}
                    if(after.Count!=before.Count)changed=true;
                    if(changed)save();
                } catch(Exception failure) {
                    try {restore(before);save();}
                    catch(Exception rollback){throw new AggregateException("Settings write and rollback failed; retain configuration.",failure,rollback);}
                    throw;
                }
            });
        }
        internal Task Enqueue(Action action)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock(sync) {
                if(sealedWriter){done.SetException(new InvalidOperationException("Settings writer is closing."));return done.Task;}
                if(work.Count>=32) {done.SetException(new InvalidOperationException("Settings writer is busy; retry later."));return done.Task;}
                work.Enqueue(delegate {try {action();done.SetResult(true);}catch(Exception ex){done.SetException(ex);}});
                if(!running) {
                    running=true;
                    if(!ThreadPool.QueueUserWorkItem(delegate { Drain(); })) {running=false;work.Dequeue();done.SetException(new InvalidOperationException("Settings worker unavailable"));}
                }
            }
            return done.Task;
        }
        internal Task SealAsync()
        {
            lock(sync) {
                if(sealedCompletion!=null)return sealedCompletion;
                sealedWriter=true;
                if(!running){sealedCompletion=Task.FromResult(true);return sealedCompletion;}
                var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                work.Enqueue(delegate {done.SetResult(true);});sealedCompletion=done.Task;return sealedCompletion;
            }
        }
        private void Drain()
        {
            for(;;) {
                Action action;
                lock(sync) {if(work.Count==0) {running=false;return;} action=work.Dequeue();}
                action();
            }
        }
    }
}
