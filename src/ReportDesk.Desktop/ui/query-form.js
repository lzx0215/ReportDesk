'use strict';
// In-memory values belong to the report session, not to the lifetime of a view.
window.QueryState = {
  create(details, definitionText, textParameterNames) {
    // Existing read-only definition IPC; no SQL execution or rewriting. Text aliases
    // used by any query in this report are conservatively treated as business values.
    const textParameters = window.reportDesk?.isWeb
      ? (Array.isArray(textParameterNames) ? textParameterNames : []).filter(name => typeof name === 'string').map(name => name.toLowerCase())
      : [...definitionText.matchAll(/&([\p{L}_][\p{L}\p{Nd}_]*)\.Text\b/gu)].map(m => m[1].toLowerCase());
    return { reportId: details.id, source: details.source, details, parameterValues: new Map(), textParameters, lastExecutedQuerySnapshot: null };
  },
  capture(input, definition) {
    if (definition.multiple) {
      const selections = JSON.parse(input.dataset.selections || '[]');
      return { value: selections.map(c => c.value), text: selections.map(c => c.label).join(','), selections };
    }
    return {
      value: input.type === 'checkbox' ? (input.checked ? 'True' : 'False') : input.tagName === 'SELECT' && !input.selectedOptions[0]?.dataset.choice ? null : input.value,
      text: input.selectedOptions?.[0]?.dataset.label ?? input.selectedOptions?.[0]?.textContent ?? input.value
    };
  },
  values(session) {
    const entries = [];
    for (const [name, state] of session.parameterValues) entries.push([name, Array.isArray(state.value) ? [...state.value] : state.value], [name + '.Text', state.text]);
    return Object.fromEntries(entries);
  },
  payload(session) { return { reportId: session.reportId, source: session.source, values: this.values(session) }; },
  key(payload, session) {
    const definitions = new Map(session.details.parameters.map(p => [p.name, p]));
    const values = Object.keys(payload.values).sort().filter(name => !name.endsWith('.Text') || session.textParameters.includes(name.slice(0, -5).toLowerCase())).map(name => {
      let value = payload.values[name];
      if (definitions.get(name)?.kind === 'DateTimeType' && typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d$/.test(value)) value += ':00';
      return [name, value];
    });
    return JSON.stringify([payload.reportId, payload.source, values]);
  },
  dirty(session) {
    return !!session?.lastExecutedQuerySnapshot && this.key(this.payload(session), session) !== this.key(session.lastExecutedQuerySnapshot, session);
  },
  summary(session) {
    if (!session) return '请选择报表';
    const source = session.details.sources.find(s => s.index === session.source);
    const parts = source ? ['数据源：' + source.name] : [];
    for (const p of session.details.parameters) {
      const state = session.parameterValues.get(p.name);
      if (p.implicitValue || !state || state.value === null || state.value === '' || (Array.isArray(state.value) && !state.value.length)) continue;
      let value = p.multiple ? state.selections.map(c => c.label || c.value).join('、') : p.kind === 'CheckBoxType' ? (state.value === 'True' ? '是' : '否') : state.text;
      if (p.kind === 'DateTimeType') value = state.value.replace('T', ' ').replaceAll('-', '/');
      parts.push(p.label + '：' + value);
    }
    return parts.join(' · ') || '当前报表无查询条件';
  },
  isParameterError(message) {
    const first = message.split('\n')[0].trim();
    return ['请填写或选择所有查询条件。', '请填写已确认的上下文编码。', '日期格式无效。', '请输入已确认的住院流水号 RegisterID；不能填写住院号或姓名。'].includes(first) ||
      ['请加载并选择多选条件：', '请至少选择一个编码：', '缺少控件显示名称：', '复选框值无效：', '请选择配置中的选项：'].some(prefix => first.startsWith(prefix));
  }
};

window.QueryConditionsView = class QueryConditionsView {
  constructor(panel, parameters, source, onChange, onLookup) {
    this.parameters = parameters;
    this.source = source;
    this.onChange = onChange;
    this.onLookup = onLookup;
    this.fields = new Map();
    panel.insertBefore(this.field({ label: '数据源' }, source), parameters);
  }

  clear() { this.session = null; this.fields.clear(); this.parameters.replaceChildren(); this.source.replaceChildren(); }

  mount(session) {
    this.clear(); this.session = session;
    for (const source of session.details.sources) this.source.add(new Option(source.name, source.index));
    this.source.value = String(session.source);
    for (const p of session.details.parameters) {
      const input = this.createControl(p);
      const saved = session.parameterValues.get(p.name);
      if (saved) {
        if (p.multiple) { input.dataset.selections = JSON.stringify(saved.selections); input.value = saved.selections.length ? `已选择 ${saved.selections.length} 项` : ''; }
        else if (input.type === 'checkbox') input.checked = saved.value === 'True';
        else if (input.tagName === 'SELECT') {
          if (saved.value !== null) {
            const existing = [...input.options].findIndex(o => o.dataset.choice && o.value === saved.value && (o.dataset.label ?? o.textContent) === saved.text);
            if (existing >= 0) input.selectedIndex = existing;
            else { const option = new Option(saved.text, saved.value); option.dataset.choice = 'true'; option.dataset.label = saved.text; input.add(option); input.selectedIndex = input.options.length - 1; }
          }
        } else input.value = saved.value ?? '';
      } else session.parameterValues.set(p.name, window.QueryState.capture(input, p));
      input.dataset.parameter = p.name;
      this.fields.set(p.name, { input, definition: p });
      for (const event of ['input', 'change']) input.addEventListener(event, () => this.sync(p.name));
      let auxiliary;
      if ((p.kind === 'ComboBoxType' || p.treeSelect) && p.lookup) {
        auxiliary = document.createElement('button'); auxiliary.type = 'button'; auxiliary.textContent = '加载选项'; auxiliary.title = '加载选项'; auxiliary.setAttribute('aria-label', '加载选项：' + p.label);
        auxiliary.onclick = () => this.onLookup(p);
      }
      this.parameters.append(this.field(p, input, auxiliary));
    }
  }

  // Extracted from renderer.select: preserve initialization and control semantics.
  createControl(p) {
    let input;
    if (p.multiple) { input=document.createElement('input'); input.readOnly=true; input.placeholder='请加载并选择编码'; input.dataset.selections='[]'; }
    else if (p.kind === 'ComboBoxType' || p.treeSelect) { input = document.createElement('select'); input.add(new Option('请选择', '')); input.options[0].disabled = true; const choices = [...(p.hasAll && !p.treeSelect ? [{Value:p.allValue,Label:p.allLabel || '全部'}] : []), ...(p.options || [])]; for (const choice of choices) { const option = new Option(choice.Label, choice.Value); option.dataset.choice = 'true'; input.add(option); } input.selectedIndex = 0; }
    else { input = document.createElement('input'); if (p.kind === 'DateTimeType') { const dateOnly = !/[Hhmsft]/.test(p.format); input.type = dateOnly ? 'date' : 'datetime-local'; input.step = '1'; input.value = dateOnly ? p.initial.slice(0,10) : p.initial; } else if (p.kind === 'CheckBoxType') { input.type = 'checkbox'; input.checked = p.initial === 'True'; } else { input.type = 'text'; input.value = p.initial || ''; } }
    return input;
  }

  sync(name) {
    const field = this.fields.get(name);
    this.session.parameterValues.set(name, window.QueryState.capture(field.input, field.definition));
    this.onChange();
  }

  field(definition, control, auxiliary) {
    const field = document.createElement('div');
    field.className = 'query-field';
    const label = document.createElement('label');
    label.className = 'query-field-label';
    const text = definition.label + (definition.implicitValue ? '（已确认编码）' : '');
    if (control.type === 'checkbox') {
      field.classList.add('query-check');
      const caption = document.createElement('span'); caption.textContent = text;
      label.append(control, caption); field.append(label);
    } else {
      if (!control.id) control.id = 'query-control-' + this.parameters.childElementCount;
      label.htmlFor = control.id; label.textContent = text;
      control.classList.add('query-control');
      // Hover/focus also covers values assigned by existing lookup handlers.
      const updateTitle = () => { control.title = control.tagName === 'SELECT' ? control.selectedOptions[0]?.textContent || '' : control.value; };
      for (const event of ['input', 'change', 'mouseenter', 'focus']) control.addEventListener(event, updateTitle);
      updateTitle();
      const row = document.createElement('div'); row.className = 'query-field-control-row';
      row.append(control);
      if (auxiliary) { field.classList.add('has-auxiliary'); auxiliary.classList.add('query-load'); row.append(auxiliary); }
      field.append(label, row);
    }
    return field;
  }

};
