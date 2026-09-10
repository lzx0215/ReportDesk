const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('reportDesk', {
  call: (method, args = {}) => ipcRenderer.invoke('reportdesk:call', method, args),
  onProgress: callback => { const listener = (_event, message) => callback(message); ipcRenderer.on('reportdesk:progress', listener); return () => ipcRenderer.removeListener('reportdesk:progress', listener); },
  onFatal: callback => { const listener = (_event, message) => callback(message); ipcRenderer.on('reportdesk:fatal', listener); return () => ipcRenderer.removeListener('reportdesk:fatal', listener); }
});
