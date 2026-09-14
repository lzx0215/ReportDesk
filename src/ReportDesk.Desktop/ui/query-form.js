'use strict';
// Presentation only: callers retain control creation, values and event handlers.
window.QueryFieldsPanel = class QueryFieldsPanel {
  constructor(panel, parameters, source, action) {
    this.parameters = parameters;
    panel.classList.add('query-fields-panel');
    panel.insertBefore(this.field({ label: '数据源', kind: 'ComboBoxType' }, source), parameters);
    action.classList.add('query-action');
    panel.append(action);
  }

  size(definition) {
    let size = 'm';
    if (definition.multiple && definition.treeSelect) size = 'xl';
    else if (definition.multiple || definition.treeSelect || definition.implicitValue || definition.kind === 'RegisterIdType') size = 'l';
    else if (definition.kind === 'CheckBoxType') size = 's';
    // Long labels get at most one promotion; never infer a business type from text.
    const length = Array.from(definition.label || '').reduce((n, c) => n + (c.codePointAt(0) > 255 ? 2 : 1), 0);
    if (size === 's' && length > 18) size = 'm';
    else if (size === 'm' && length > 32) size = 'l';
    return size;
  }

  field(definition, control, auxiliary) {
    const field = document.createElement('div');
    field.className = 'query-field size-' + this.size(definition);
    if (definition.kind === 'DateTimeType' && /[Hhmsft]/.test(definition.format || '')) field.classList.add('has-time');
    const label = document.createElement('label');
    const text = definition.label + (definition.implicitValue ? '（已确认编码）' : '');
    if (control.type === 'checkbox') {
      field.classList.add('query-check');
      const caption = document.createElement('span'); caption.textContent = text;
      label.append(control, caption); field.append(label);
    } else {
      if (!control.id) control.id = 'query-control-' + this.parameters.childElementCount;
      label.htmlFor = control.id; label.textContent = text;
      const row = document.createElement('div'); row.className = 'control-row';
      row.append(control);
      if (auxiliary) row.append(auxiliary);
      field.append(label, row);
    }
    return field;
  }

  append(definition, control, auxiliary) {
    this.parameters.append(this.field(definition, control, auxiliary));
  }
};
