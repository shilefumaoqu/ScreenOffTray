using System;
using System.Collections.Generic;

namespace ScreenOffTray
{
    internal interface IHotkeyRegistrar
    {
        bool TryRegister(int id, HotkeyBinding binding, out string error);
        bool Unregister(int id);
    }

    // UI-thread transaction state, independent of HWNDs, hooks, settings and display work.
    internal sealed class HotkeyRegistration : IDisposable
    {
        private readonly IHotkeyRegistrar registrar;
        private readonly Dictionary<int, HotkeyBinding> reservations = new Dictionary<int, HotkeyBinding>();
        private int activeId, nextId = 0x5101;
        private bool disposed;
        internal HotkeyBinding Hotkey { get; private set; }
        internal bool Registered { get { return activeId != 0; } }
        internal bool Changing { get; private set; }
        internal bool CleanupPending { get { return reservations.Count > (Registered ? 1 : 0); } }

        internal HotkeyRegistration(IHotkeyRegistrar registrar, HotkeyBinding initial)
        {
            if (registrar == null) throw new ArgumentNullException("registrar");
            if (initial == null) throw new ArgumentNullException("initial");
            this.registrar = registrar; Hotkey = initial;
        }

        internal bool Initialize(out string error)
        {
            return TryChange(Hotkey, delegate { }, false, false, out error);
        }

        // persist must save atomically and restore its own in-memory settings on
        // exception. Action cannot undo arbitrary external writes by its caller.
        // The previous registration stays alive throughout persist; it cannot be
        // stolen between unregister and a failed save's rollback.
        internal bool TryChange(HotkeyBinding binding, Action persist, bool busy, bool heldInput, out string error)
        {
            error = null;
            if (disposed) { error = "会话已关闭 / Session is closed."; return false; }
            if (busy || heldInput || Changing) {
                error = "请先完成恢复并松开按键 / Finish recovery and release held input first."; return false;
            }
            if (binding == null || persist == null) {
                error = "快捷键或保存操作无效 / Invalid hotkey or save operation."; return false;
            }
            Reservation ticket;
            if(!TryReserve(binding,busy,heldInput,out ticket,out error)) return false;
            using(ticket) {
                try { persist(); ticket.Commit(); return true; }
                catch(Exception ex) { error="快捷键保存失败，原快捷键已保留 / Hotkey save failed; previous binding retained. "+ex.Message; return false; }
            }
        }
        internal sealed class Reservation : IDisposable
        {
            private HotkeyRegistration owner; private readonly int candidate; private readonly HotkeyBinding binding;
            internal Reservation(HotkeyRegistration owner,int candidate,HotkeyBinding binding) {this.owner=owner;this.candidate=candidate;this.binding=binding;}
            internal void Commit() {
                if(owner==null)throw new InvalidOperationException("Hotkey reservation ended");
                owner.Hotkey=binding;owner.activeId=candidate;owner.CleanupInactive();owner.Changing=false;owner=null;
            }
            public void Dispose() { if(owner==null)return; if(candidate!=owner.activeId)owner.TryRelease(candidate);owner.Changing=false;owner=null; }
        }
        internal bool TryReserve(HotkeyBinding binding,bool busy,bool heldInput,out Reservation ticket,out string error)
        {
            ticket=null;error=null;
            if(disposed || busy || heldInput || Changing || binding==null) {error="请先完成恢复并松开按键 / Finish recovery and release held input first.";return false;}
            int candidate=0;
            foreach(var item in reservations) if(item.Value.SameAs(binding)) {candidate=item.Key;break;}
            if(candidate==0) {
                candidate=AllocateId();
                if(candidate==0){error="Hotkey registration resources exhausted";return false;}
                if(!registrar.TryRegister(candidate,binding,out error))return false;
                reservations.Add(candidate,binding);
            }
            Changing=true;ticket=new Reservation(this,candidate,binding);return true;
        }

        private int AllocateId()
        {
            for (int i = 0; i <= 0xBFFF - 0x5101; i++) {
                int id = nextId;
                nextId = nextId == 0xBFFF ? 0x5101 : nextId + 1;
                if (!reservations.ContainsKey(id)) return id;
            }
            return 0;
        }
        private void TryRelease(int id)
        {
            try { if (registrar.Unregister(id)) reservations.Remove(id); }
            catch { /* Retain ownership for retry; active binding is unchanged. */ }
        }
        private void CleanupInactive()
        {
            List<int> ids = new List<int>(reservations.Keys);
            foreach (int id in ids) if (id != activeId) TryRelease(id);
        }
        public void Dispose()
        {
            if (Changing) throw new InvalidOperationException("快捷键正在保存 / Hotkey save is in progress.");
            disposed = true; activeId = 0;
            CleanupInactive();
        }
    }
}
