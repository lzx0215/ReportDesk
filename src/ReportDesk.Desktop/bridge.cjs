const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');
class Bridge {
  constructor(executable, dataDirectory, configDirectory, offline, progress, fatal) {
    this.sequence = 0; this.pending = new Map(); this.active = null;
    this.process = spawn(executable, [dataDirectory, configDirectory, ...(offline ? ['--offline'] : [])], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    this.ready = new Promise((resolve, reject) => { this.resolveReady = resolve; this.rejectReady = reject; });
    this.ready.catch(() => {});
    const fail = () => { if (this.dead) return; this.dead = true; const error = new Error('后台进程不可用，请关闭并重新启动 ReportDesk。'); this.rejectReady(error); for (const entry of this.pending.values()) entry.reject(error); this.pending.clear(); this.active = null; if (!this.stopping) fatal(error.message); };
    this.process.on('error', fail); this.process.on('exit', fail);
    this.process.stdin.on('error', fail);
    // Never log raw stderr: driver/runtime messages may contain sensitive information.
    this.process.stderr.resume();
    createInterface({ input: this.process.stdout }).on('line', line => {
      let msg; try { msg = JSON.parse(line); } catch { fail(); return; }
      if (msg.type === 'ready') { if (msg.protocol !== 1) fail(); else this.resolveReady(); return; }
      if (msg.type === 'fatal') { this.rejectReady(new Error(msg.message)); fail(); return; }
      if (msg.type === 'progress') { if (msg.operationId === this.active) progress(msg.message); return; }
      const entry = this.pending.get(msg.id); if (!entry) return;
      this.pending.delete(msg.id); if (this.active === msg.id) this.active = null;
      if (msg.ok) entry.resolve(msg.data); else { const error = new Error(msg.message || '后台操作失败。'); error.cancelled = !!msg.cancelled; entry.reject(error); }
    });
  }
  async call(method, args = {}) {
    await this.ready; if (this.dead) throw new Error('后台进程已退出。');
    if (method !== 'cancel' && this.active !== null) throw new Error('正在处理另一项操作。');
    const id = ++this.sequence;
    if (method !== 'cancel') this.active = id;
    return new Promise((resolve, reject) => { this.pending.set(id, { resolve, reject }); this.process.stdin.write(JSON.stringify({ id, method, args }) + '\n'); });
  }
  cancel() { return this.active === null ? Promise.resolve({}) : this.call('cancel', { operationId: this.active }); }
  close() { this.stopping = true; this.process.stdin.end(); }
}
module.exports = { Bridge };
