const { createApp, ref, onMounted, onUnmounted, computed, watch } = Vue;
const { ElMessage, ElLoading, ElMessageBox } = ElementPlus;

const app = createApp({
  setup() {
    const isLoggedIn = ref(false);
    const isLoggingIn = ref(false);
    const loginForm = ref({ username: '', password: '' });
    const showPwdModal = ref(false);
    const pwdForm = ref({ old: '', new: '', confirm: '' });
    const currentRole = ref('');
    let csrfToken = '';

    const activeNav = ref('1');
    const isCollecting = ref(false);
    const groupList = ref([]);
    const activeDashboardGroups = ref([]);
    const protocolOptions = ref([]);
    const mqttTemplates = ref([]);
    const networkInterfaces = ref([]);
    const networkEffectiveBindings = ref([]);
    const networkBindings = ref({ plcInterfaceId: null, mqttInterfaceId: null, webInterfaceId: null });
    const isRefreshingNetwork = ref(false);
    const isSavingNetwork = ref(false);
    const isRunningNetworkDiagnostic = ref(false);
    const networkDiagnosticType = ref('ping');
    const networkDiagnosticResult = ref(null);
    const networkDiagnosticForm = ref({
      host: '127.0.0.1', port: 502, usage: 'PlcAcquisition', deviceId: '', timeoutMs: 3000
    });
    const resource = ref({ cpuPercent: 0, workingSetMb: 0 });
    const mqttRuntime = ref({
      state: 'Disabled', enabled: false, host: '', port: 0, changedAt: null,
      lastAttemptAt: null, connectedAt: null, nextRetryAt: null, lastError: null
    });
    const isApplyingMqtt = ref(false);
    const realtimeConnected = ref(false);
    const runtimeByDevice = new Map();
    const deviceHealthByDevice = new Map();
    const tagSamplesById = new Map();
    let runtimeTimer = null;
    let mqttRuntimeTimer = null;
    let signalRRestartTimer = null;
    let hubConnection = null;
    let suppressAcquisitionChange = false;

    const api = async (path, options = {}) => {
      const request = { ...options, headers: { Accept: 'application/json', ...(options.headers || {}) } };
      if (csrfToken) request.headers['X-CSRF-TOKEN'] = csrfToken;
      if (request.body && !(request.body instanceof FormData)) {
        request.headers['Content-Type'] = 'application/json';
        if (typeof request.body !== 'string') request.body = JSON.stringify(request.body);
      }

      const response = await fetch(path, request);
      const contentType = response.headers.get('content-type') || '';
      const data = response.status === 204
        ? null
        : contentType.includes('application/json') ? await response.json() : await response.text();
      if (!response.ok) {
        const detail = data?.error || data?.title || data?.errors?.map(item => item.message || item).join('；') || `HTTP ${response.status}`;
        const error = new Error(detail);
        error.data = data;
        throw error;
      }

      return data;
    };

    const deviceStateNames = ['Stopped', 'Connecting', 'Online', 'Degraded', 'Reconnecting', 'Offline'];
    const normalizeDeviceState = state => typeof state === 'number' ? deviceStateNames[state] || 'Offline' : state;
    const runtimeStatusCode = state => normalizeDeviceState(state) === 'Online' ? 1 : normalizeDeviceState(state) === 'Degraded' ? 2 : 0;
    const createDeviceView = (device, groupName) => ({
      ...device,
      nodeType: 'device',
      groupName,
      ipAddress: `${device.host}:${device.port}`,
      status: runtimeStatusCode(runtimeByDevice.get(device.id)),
      runtimeState: normalizeDeviceState(runtimeByDevice.get(device.id)),
      deviceExternalId: deviceHealthByDevice.get(device.id)?.deviceExternalId || device.mqttDeviceId || device.name,
      deviceErrorCode: deviceHealthByDevice.get(device.id)?.errorCode ?? 1001,
      deviceHealthy: deviceHealthByDevice.get(device.id)?.isHealthy ?? false
    });

    const applyDeviceHealth = (statuses, clear = false) => {
      if (clear) deviceHealthByDevice.clear();
      for (const status of statuses || []) deviceHealthByDevice.set(status.deviceId, status);
    };

    const updateDeviceRuntimeViews = () => {
      for (const group of groupList.value) {
        for (const device of group.children || []) {
          const health = deviceHealthByDevice.get(device.id);
          device.status = runtimeStatusCode(runtimeByDevice.get(device.id));
          device.runtimeState = normalizeDeviceState(runtimeByDevice.get(device.id));
          device.deviceExternalId = health?.deviceExternalId || device.mqttDeviceId || device.name;
          device.deviceErrorCode = health?.errorCode ?? 1001;
          device.deviceHealthy = health?.isHealthy ?? false;
        }
      }
    };

    const applySnapshot = snapshot => {
      mqttConfig.value = { ...mqttConfig.value, ...snapshot.mqtt };
      groupList.value = snapshot.groups.map(group => ({
        id: group.id,
        name: group.name,
        line: group.line,
        nodeType: 'group',
        topic: group.topic,
        uploadCycle: group.uploadCycleSeconds,
        scanCycle: group.scanCycleMs,
        enabled: group.enabled,
        children: snapshot.devices
          .filter(device => device.groupId === group.id)
          .map(device => createDeviceView(device, group.name))
      }));
      activeDashboardGroups.value = groupList.value.map(group => group.id);
    };

    const refreshRuntime = async () => {
      try {
        const dashboard = await api('/api/v1/dashboard');
        isCollecting.value = dashboard.isRunning;
        resource.value = dashboard.resource || resource.value;
        runtimeByDevice.clear();
        for (const status of dashboard.runtime || []) runtimeByDevice.set(status.deviceId, status.state);
        applyDeviceHealth(dashboard.deviceHealth, true);
        updateDeviceRuntimeViews();
      } catch (error) {
        ElMessage.error(`运行状态刷新失败：${error.message}`);
      }
    };

    const refreshMqttRuntime = async () => {
      try {
        mqttRuntime.value = await api('/api/v1/mqtt/runtime');
      } catch {
      }
    };

    const applyRuntimeMessage = message => {
      isCollecting.value = message.isRunning;
      resource.value = message.resource || resource.value;
      if (message.isSnapshot) runtimeByDevice.clear();
      for (const status of message.devices || []) runtimeByDevice.set(status.deviceId, status.state);
      applyDeviceHealth(message.deviceHealth, message.isSnapshot);
      updateDeviceRuntimeViews();
    };

    const qualityNames = ['Good', 'Uncertain', 'Bad', 'Disconnected', 'Timeout', 'ConfigurationError'];
    const normalizeQuality = quality => typeof quality === 'number' ? qualityNames[quality] || 'Bad' : quality;

    const formatSampleValue = value => {
      if (value === null || value === undefined) return '—';
      return typeof value === 'object' ? JSON.stringify(value) : String(value);
    };

    const applyTagSamples = samples => {
      for (const sample of samples || []) tagSamplesById.set(sample.tagId, sample);
      if (!currentDevice.value) return;
      for (const point of tagList.value) {
        const sample = tagSamplesById.get(point.id);
        if (!sample) continue;
        point.rawVal = formatSampleValue(sample.rawValue);
        point.finalVal = formatSampleValue(sample.value);
        point.quality = normalizeQuality(sample.quality);
        point.sampleError = sample.error || '';
        point.sampledAt = sample.timestamp;
      }
    };

    const scheduleRealtimeRestart = () => {
      if (signalRRestartTimer || !isLoggedIn.value) return;
      signalRRestartTimer = setTimeout(async () => {
        signalRRestartTimer = null;
        await startRealtime();
      }, 30000);
    };

    const startRealtime = async () => {
      if (!hubConnection) {
        hubConnection = new signalR.HubConnectionBuilder()
          .withUrl('/hubs/status')
          .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
          .configureLogging(signalR.LogLevel.Warning)
          .build();
        hubConnection.serverTimeoutInMilliseconds = 15000;
        hubConnection.keepAliveIntervalInMilliseconds = 5000;
        hubConnection.on('runtimeStatus', applyRuntimeMessage);
        hubConnection.on('tagSamples', applyTagSamples);
        hubConnection.onreconnecting(() => { realtimeConnected.value = false; });
        hubConnection.onreconnected(async () => {
          realtimeConnected.value = true;
          await subscribeCurrentDevice();
        });
        hubConnection.onclose(() => {
          realtimeConnected.value = false;
          scheduleRealtimeRestart();
        });
      }

      if (hubConnection.state !== signalR.HubConnectionState.Disconnected) return;
      try {
        await hubConnection.start();
        realtimeConnected.value = true;
        await subscribeCurrentDevice();
      } catch {
        realtimeConnected.value = false;
        scheduleRealtimeRestart();
      }
    };

    const stopRealtime = async () => {
      if (signalRRestartTimer) clearTimeout(signalRRestartTimer);
      signalRRestartTimer = null;
      realtimeConnected.value = false;
      if (hubConnection && hubConnection.state !== signalR.HubConnectionState.Disconnected) {
        await hubConnection.stop();
      }
    };

    const loadConfiguration = async () => {
      const [protocols, snapshot, dashboard, network, templates, mqttRuntimeStatus] = await Promise.all([
        api('/api/v1/protocols'),
        api('/api/v1/configuration/draft'),
        api('/api/v1/dashboard'),
        api('/api/v1/network/configuration'),
        api('/api/v1/mqtt/templates'),
        api('/api/v1/mqtt/runtime')
      ]);
      protocolOptions.value = protocols;
      mqttTemplates.value = templates;
      isCollecting.value = dashboard.isRunning;
      resource.value = dashboard.resource || resource.value;
      mqttRuntime.value = mqttRuntimeStatus;
      runtimeByDevice.clear();
      for (const status of dashboard.runtime || []) runtimeByDevice.set(status.deviceId, status.state);
      applyDeviceHealth(dashboard.deviceHealth, true);
      applySnapshot(snapshot);
      applyNetworkConfiguration(network);
    };

    const applyNetworkConfiguration = network => {
      networkInterfaces.value = network.interfaces || [];
      networkEffectiveBindings.value = network.effectiveBindings || [];
      networkBindings.value = {
        plcInterfaceId: network.bindings?.plcInterfaceId || null,
        mqttInterfaceId: network.bindings?.mqttInterfaceId || null,
        webInterfaceId: network.bindings?.webInterfaceId || null
      };
      if (!networkDiagnosticForm.value.deviceId) {
        networkDiagnosticForm.value.deviceId = groupList.value.flatMap(group => group.children || [])[0]?.id || '';
      }
    };

    const activateSession = async session => {
      csrfToken = session.csrfToken;
      currentRole.value = session.role;
      loginForm.value.username = session.username;
      await loadConfiguration();
      isLoggedIn.value = true;
      await startRealtime();
      if (runtimeTimer) clearInterval(runtimeTimer);
      runtimeTimer = setInterval(refreshRuntime, 60000);
      if (mqttRuntimeTimer) clearInterval(mqttRuntimeTimer);
      mqttRuntimeTimer = setInterval(refreshMqttRuntime, 3000);
    };

    const handleLogin = async () => {
      if (!loginForm.value.username || !loginForm.value.password) return ElMessage.warning('请输入账号和密码！');
      isLoggingIn.value = true;
      try {
        const antiforgery = await api('/api/auth/antiforgery');
        csrfToken = antiforgery.token;
        await api('/api/auth/login', {
          method: 'POST',
          body: { username: loginForm.value.username, password: loginForm.value.password }
        });
        const session = await api('/api/auth/session');
        await activateSession(session);
        ElMessage.success(`安全登录成功，当前角色：${session.role}。`);
        if (session.mustChangePassword) {
          pwdForm.value = { old: loginForm.value.password, new: '', confirm: '' };
          showPwdModal.value = true;
          ElMessage.warning('首次登录必须修改默认密码。');
        }
      } catch (error) {
        csrfToken = '';
        ElMessage.error(`登录失败：${error.message}`);
      } finally {
        isLoggingIn.value = false;
      }
    };

    const restoreSession = async () => {
      try {
        const session = await api('/api/auth/session');
        await activateSession(session);
        if (session.mustChangePassword) {
          pwdForm.value = { old: '', new: '', confirm: '' };
          showPwdModal.value = true;
          ElMessage.warning('当前账号必须修改初始密码。');
        }
      } catch {
        isLoggedIn.value = false;
      }
    };

    const handleUserCommand = async command => {
      if (command === 'logout') {
        try {
          await api('/api/auth/logout', { method: 'POST' });
        } finally {
          isLoggedIn.value = false;
          loginForm.value.password = '';
          currentRole.value = '';
          csrfToken = '';
          await stopRealtime();
          if (runtimeTimer) clearInterval(runtimeTimer);
          runtimeTimer = null;
          if (mqttRuntimeTimer) clearInterval(mqttRuntimeTimer);
          mqttRuntimeTimer = null;
          ElMessage.success('服务端会话已注销');
        }
      }
      if (command === 'changePwd') {
        pwdForm.value = { old: '', new: '', confirm: '' };
        showPwdModal.value = true;
      }
    };

    const savePassword = async () => {
      if (!pwdForm.value.old || !pwdForm.value.new || !pwdForm.value.confirm) return ElMessage.warning('请填写完整！');
      if (pwdForm.value.new !== pwdForm.value.confirm) return ElMessage.error('两次新密码不一致！');
      try {
        await api('/api/auth/change-password', {
          method: 'POST',
          body: { currentPassword: pwdForm.value.old, newPassword: pwdForm.value.new }
        });
        loginForm.value.password = '';
        showPwdModal.value = false;
        ElMessage.success('服务端密码已安全更新。');
      } catch (error) {
        ElMessage.error(`密码修改失败：${error.message}`);
      }
    };

    const toggleAcquisition = async enabled => {
      if (suppressAcquisitionChange) return;
      try {
        await api(`/api/v1/acquisition/${enabled ? 'start' : 'stop'}`, { method: 'POST' });
        ElMessage.success(enabled ? '采集引擎已启动' : '采集引擎已停止');
      } catch (error) {
        suppressAcquisitionChange = true;
        isCollecting.value = !enabled;
        suppressAcquisitionChange = false;
        ElMessage.error(`采集控制失败：${error.message}`);
      }
    };

    const totalDevices = computed(() => groupList.value.reduce((sum, group) => sum + (group.children?.length || 0), 0));
    const onlineDevices = computed(() => groupList.value.reduce((sum, group) => sum + (group.children?.filter(device => device.status === 1).length || 0), 0));
    const offlineDevices = computed(() => groupList.value.reduce(
      (sum, group) => sum + (group.children?.filter(device => device.status !== 1).length || 0), 0));
    const deviceStatusType = device => {
      if (!isCollecting.value || device.runtimeState === 'Stopped') return 'info';
      if (device.runtimeState === 'Online') return 'success';
      if (['Connecting', 'Degraded', 'Reconnecting'].includes(device.runtimeState)) return 'warning';
      return 'danger';
    };
    const deviceStatusLabel = device => {
      if (!isCollecting.value || device.runtimeState === 'Stopped') return '⏸️ 引擎已挂起';
      return ({
        Connecting: '🟡 连接确认中',
        Online: '🟢 正常采集中',
        Degraded: '🟡 部分点位异常',
        Reconnecting: '🟠 通讯重连中',
        Offline: '🔴 设备离线'
      })[device.runtimeState] || '🔴 状态未知';
    };
    const deviceHealthType = device => device.deviceHealthy ? 'success' : 'danger';
    const deviceHealthLabel = device => device.deviceHealthy ? '正常' : '异常';

    const showAddGroupModal = ref(false);
    const isEditGroup = ref(false);
    const newGroup = ref({ id: '', name: '', line: '', topic: '', uploadCycle: 10, scanCycle: 1000 });

    const openAddGroupModal = (row = null) => {
      isEditGroup.value = Boolean(row?.id);
      newGroup.value = row?.id
        ? { id: row.id, name: row.name, line: row.line || '', topic: row.topic, uploadCycle: row.uploadCycle, scanCycle: row.scanCycle }
        : { id: '', name: '', line: '', topic: 'factory/${deviceId}/data', uploadCycle: 15, scanCycle: 1000 };
      showAddGroupModal.value = true;
    };

    const saveGroup = async () => {
      const model = newGroup.value;
      if (!model.name?.trim() || !model.topic?.trim()) return ElMessage.warning('分组名称与 Topic 不能为空。');
      if (!Number.isFinite(model.scanCycle) || model.scanCycle < 100 || model.scanCycle > 86400000) {
        return ElMessage.warning('扫描周期必须为100ms到24小时。');
      }
      if (!Number.isFinite(model.uploadCycle) || model.uploadCycle < 1 || model.uploadCycle > 86400) {
        return ElMessage.warning('上传周期必须为1秒到24小时。');
      }
      const id = model.id || crypto.randomUUID();
      const payload = {
        id,
        name: model.name.trim(),
        line: model.line?.trim() || null,
        topic: model.topic.trim(),
        scanCycleMs: model.scanCycle,
        uploadCycleSeconds: model.uploadCycle,
        enabled: true
      };
      try {
        await api(model.id ? `/api/v1/groups/${id}` : '/api/v1/groups', { method: model.id ? 'PUT' : 'POST', body: payload });
        showAddGroupModal.value = false;
        await loadConfiguration();
        ElMessage.success('分组已保存到配置草稿。');
      } catch (error) {
        ElMessage.error(`分组保存失败：${error.message}`);
      }
    };

    const deleteGroup = row => {
      ElMessageBox.confirm(`确定删除分组 [${row.name}] 及其草稿设备和点位吗？`, '危险操作', { type: 'error' })
        .then(async () => {
          await api(`/api/v1/groups/${row.id}`, { method: 'DELETE' });
          await loadConfiguration();
          ElMessage.success('分组已从草稿删除。');
        })
        .catch(error => { if (error instanceof Error) ElMessage.error(error.message); });
    };

    const showDeviceModal = ref(false);
    const isEditDevice = ref(false);
    const isTestingConnection = ref(false);
    const connectionDiagnostic = ref(null);
    const newDevice = ref(emptyDevice());
    let targetGroupForDevice = null;

    function emptyDevice() {
      return {
        id: '', name: '', mqttDeviceId: '', mqttTemplateId: null, protocol: 'SiemensS7', host: '', port: 102, enabled: true,
        connectTimeoutMs: 3000, receiveTimeoutMs: 3000
      };
    }

    const protocolDescriptor = protocol => protocolOptions.value.find(item => item.value === protocol);
    const isMitsubishiA1E = device =>
      device?.protocol === 'MitsubishiMc'
      && ['Binary1E', 'Ascii1E'].includes(device.mitsubishiFormat);
    const normalizeMitsubishiTransport = device => {
      if (isMitsubishiA1E(device)) device.mitsubishiTransport = 'Tcp';
    };
    const shouldShowProtocolFieldForDevice = (field, device) => {
      if (device?.protocol === 'SiemensS7'
          && ['rack', 'slot'].includes(field.name)
          && device.siemensAutoRackSlot) return false;
      if (isMitsubishiA1E(device)
          && ['mitsubishiTransport', 'networkNumber', 'networkStationNumber', 'targetIoStation'].includes(field.name)) return false;
      return true;
    };
    const selectedProtocolFields = computed(() => protocolDescriptor(newDevice.value.protocol)?.parameters || []);
    const currentDeviceProtocolFields = computed(() =>
      (protocolDescriptor(currentDevice.value?.protocol)?.parameters || [])
        .filter(field => shouldShowProtocolFieldForDevice(field, currentDevice.value)));
    const currentPointAddressGuide = computed(() => protocolDescriptor(currentDevice.value?.protocol)?.pointAddressGuide || null);
    const currentProtocolLabel = computed(() => protocolDescriptor(currentDevice.value?.protocol)?.label || currentDevice.value?.protocol || '');
    const siemensRecommendation = computed(() => {
      if (newDevice.value.protocol !== 'SiemensS7') return '';
      const series = newDevice.value.siemensSeries;
      if (!newDevice.value.siemensAutoRackSlot) return `手动使用 Rack ${newDevice.value.rack ?? 0} / Slot ${newDevice.value.slot ?? 0}`;
      if (series === 'S300') return '自动使用 Rack 0 / Slot 2';
      if (series === 'S400') return '自动使用 Rack 0 / Slot 3';
      if (series === 'S200' || series === 'S200Smart') return '自动使用 Rack 0 / Slot 0，主要依据 TSAP 建链';
      return '自动使用 Rack 0 / Slot 1';
    });

    const shouldShowProtocolField = field => shouldShowProtocolFieldForDevice(field, newDevice.value);

    watch(() => newDevice.value.mitsubishiFormat, () => normalizeMitsubishiTransport(newDevice.value));

    const applyDefaultPort = protocol => {
      const descriptor = protocolDescriptor(protocol);
      if (!descriptor) return;
      newDevice.value.port = descriptor.defaultPort;
      for (const field of descriptor.parameters || []) newDevice.value[field.name] = field.defaultValue;
    };

    const openAddDeviceModal = groupRow => {
      isEditDevice.value = false;
      targetGroupForDevice = groupRow;
      newDevice.value = emptyDevice();
      applyDefaultPort(newDevice.value.protocol);
      connectionDiagnostic.value = null;
      showDeviceModal.value = true;
    };

    const openEditDeviceModal = deviceRow => {
      isEditDevice.value = true;
      targetGroupForDevice = groupList.value.find(group => group.children?.some(device => device.id === deviceRow.id));
      newDevice.value = JSON.parse(JSON.stringify(deviceRow));
      connectionDiagnostic.value = null;
      showDeviceModal.value = true;
    };

    const devicePayload = (device, groupId) => {
      const payload = {
        id: device.id || crypto.randomUUID(),
        groupId,
        name: device.name.trim(),
        mqttDeviceId: device.mqttDeviceId?.trim() || null,
        mqttTemplateId: device.mqttTemplateId || null,
        protocol: device.protocol,
        host: device.host.trim(),
        port: Number(device.port),
        enabled: device.enabled ?? true,
        connectTimeoutMs: Number(device.connectTimeoutMs || 3000),
        receiveTimeoutMs: Number(device.receiveTimeoutMs || 3000)
      };
      for (const field of protocolDescriptor(device.protocol)?.parameters || []) {
        const value = device[field.name] ?? field.defaultValue;
        payload[field.name] = field.inputType === 'number'
          ? Number(value)
          : field.inputType === 'switch' ? Boolean(value) : value;
      }
      return payload;
    };

    const saveDevice = async () => {
      const groupId = targetGroupForDevice?.id;
      if (!groupId || !newDevice.value.name?.trim() || !newDevice.value.host?.trim()) return ElMessage.warning('设备名称和主机地址不能为空。');
      const payload = devicePayload(newDevice.value, groupId);
      try {
        await api(isEditDevice.value ? `/api/v1/devices/${payload.id}` : '/api/v1/devices', {
          method: isEditDevice.value ? 'PUT' : 'POST', body: payload
        });
        showDeviceModal.value = false;
        await loadConfiguration();
        ElMessage.success('设备已保存到配置草稿。');
      } catch (error) {
        ElMessage.error(`设备保存失败：${error.message}`);
      }
    };

    const testConnection = async () => {
      const groupId = targetGroupForDevice?.id || newDevice.value.groupId;
      if (!groupId || !newDevice.value.name?.trim() || !newDevice.value.host?.trim()) {
        return ElMessage.warning('请先填写设备名称和主机地址。');
      }
      isTestingConnection.value = true;
      connectionDiagnostic.value = null;
      try {
        const payload = devicePayload(newDevice.value, groupId);
        const result = await api('/api/v1/devices/connection-test', { method: 'POST', body: payload });
        connectionDiagnostic.value = result;
        result.success
          ? ElMessage.success('TCP链路与PLC协议握手均成功。')
          : ElMessage.error(`连接诊断失败：${result.error || result.status}`);
      } catch (error) {
        connectionDiagnostic.value = { success: false, status: 'RequestFailed', error: error.message, steps: [] };
        ElMessage.error(`连接测试失败：${error.message}`);
      } finally {
        isTestingConnection.value = false;
      }
    };

    const deleteDevice = row => {
      ElMessageBox.confirm(`确定删除设备 [${row.name}] 及其点位吗？`, '警告', { type: 'warning' })
        .then(async () => {
          await api(`/api/v1/devices/${row.id}`, { method: 'DELETE' });
          await loadConfiguration();
          ElMessage.success('设备已从配置草稿删除。');
        })
        .catch(error => { if (error instanceof Error) ElMessage.error(error.message); });
    };

    const batchCopyDevice = deviceRow => {
      ElMessageBox.prompt(`请输入要为【${deviceRow.name}】生成的克隆数量（最多100台）：`, '批量复制设备', {
        confirmButtonText: '开始生成', cancelButtonText: '取消', inputPattern: /^(?:[1-9]|[1-9]\d|100)$/, inputErrorMessage: '请输入1到100的整数'
      }).then(async ({ value }) => {
        const loading = ElLoading.service({ lock: true, text: '正在安全复制设备与点位...', background: 'rgba(0,0,0,.8)' });
        try {
          const sourceTags = await api(`/api/v1/devices/${deviceRow.id}/tags`);
          const group = groupList.value.find(item => item.children?.some(device => device.id === deviceRow.id));
          for (let index = 1; index <= Number(value); index++) {
            const clone = devicePayload(deviceRow, group.id);
            clone.id = crypto.randomUUID();
            clone.name = `${deviceRow.name}_副本${index}`;
            await api('/api/v1/devices', { method: 'POST', body: clone });
            const clonedTags = sourceTags.map(tag => ({ ...tag, id: crypto.randomUUID(), deviceId: clone.id }));
            if (clonedTags.length) await api(`/api/v1/devices/${clone.id}/tags`, { method: 'PUT', body: clonedTags });
          }
          await loadConfiguration();
          ElMessage.success(`已复制 ${value} 台设备及其点位到草稿。`);
        } catch (error) {
          ElMessage.error(`批量复制中止：${error.message}`);
        } finally {
          loading.close();
        }
      }).catch(() => {});
    };

    const drawerVisible = ref(false);
    const currentDevice = ref(null);
    watch(() => currentDevice.value?.mitsubishiFormat, () => normalizeMitsubishiTransport(currentDevice.value));
    const tagList = ref([]);
    const isSavingTags = ref(false);
    const isImportingTags = ref(false);
    const isExportingTags = ref(false);

    const subscribeCurrentDevice = async () => {
      if (!currentDevice.value || !hubConnection || hubConnection.state !== signalR.HubConnectionState.Connected) return;
      try {
        await hubConnection.invoke('SubscribeDevice', currentDevice.value.id);
      } catch (error) {
        ElMessage.warning(`实时点位订阅失败：${error.message}`);
      }
    };

    const closeTagDrawer = async () => {
      if (!currentDevice.value || !hubConnection || hubConnection.state !== signalR.HubConnectionState.Connected) return;
      try {
        await hubConnection.invoke('UnsubscribeDevice', currentDevice.value.id);
      } catch {
      }
    };

    const splitModbusAddress = address => {
      const match = /^x=(\d+);(.+)$/i.exec(address || '');
      const legacyCodes = {
        1: 'ReadCoils',
        2: 'ReadDiscreteInputs',
        3: 'ReadHoldingRegisters',
        4: 'ReadInputRegisters'
      };
      return match && legacyCodes[Number(match[1])]
        ? { functionCode: legacyCodes[Number(match[1])], address: match[2] }
        : { functionCode: 'ReadHoldingRegisters', address };
    };

    const toTagEditorRow = (tag, protocol) => {
      const modbus = protocol === 'ModbusTcp'
        ? splitModbusAddress(tag.address)
        : { functionCode: null, address: tag.address };
      return {
        ...tag,
        address: modbus.address,
        functionCode: tag.modbusFunctionCode || modbus.functionCode,
        endian: tag.byteOrder,
        onChange: tag.publishOnChange,
        rawVal: '—',
        finalVal: '—',
        quality: 'Disconnected',
        sampleError: '',
        sampledAt: null
      };
    };

    const openTagDrawer = async deviceRow => {
      currentDevice.value = deviceRow;
      drawerVisible.value = true;
      try {
        const tags = await api(`/api/v1/devices/${deviceRow.id}/tags`);
        tagList.value = tags.map(tag => toTagEditorRow(tag, deviceRow.protocol));
        applyTagSamples(tagSamplesById.values());
        await subscribeCurrentDevice();
      } catch (error) {
        ElMessage.error(`点位读取失败：${error.message}`);
      }
    };

    const addPoint = () => tagList.value.push({
      id: crypto.randomUUID(),
      name: '新测点', address: '', functionCode: 'ReadHoldingRegisters', dataType: 'Int16', endian: 'ABCD',
      operator: '*', operand: 1, decimals: 1, onChange: false, deadband: 0, stringLength: 32,
      rawVal: '—', finalVal: '—', quality: 'Disconnected', sampleError: '', sampledAt: null, remark: ''
    });

    const addPointFromExample = example => {
      const point = {
        id: crypto.randomUUID(),
        name: example.purpose || '新测点',
        address: example.address,
        functionCode: example.modbusFunctionCode || 'ReadHoldingRegisters',
        dataType: example.dataType,
        endian: 'ABCD',
        operator: '*',
        operand: 1,
        decimals: example.dataType === 'Float32' || example.dataType === 'Float64' ? 2 : 0,
        onChange: false,
        deadband: 0,
        stringLength: 32,
        rawVal: '—',
        finalVal: '—',
        quality: 'Disconnected',
        sampleError: '',
        sampledAt: null,
        remark: `${example.area} · ${example.purpose}`
      };
      tagList.value.push(point);
      ElMessage.success(`已按规范新增点位 ${example.address}，请核对后保存。`);
    };

    const calcFinalVal = point => point.finalVal ?? '—';
    const qualityLabel = quality => ({
      Good: '正常', Uncertain: '不确定', Bad: '异常', Disconnected: '断开',
      Timeout: '超时', ConfigurationError: '配置错误'
    }[quality] || quality || '无数据');

    const triggerDeviceTagImport = () => document.getElementById('device-tag-upload')?.click();

    const handleDeviceTagImport = async event => {
      const file = event.target.files?.[0];
      if (!file || !currentDevice.value || isImportingTags.value) return;
      isImportingTags.value = true;
      try {
        const form = new FormData();
        form.append('file', file);
        const preview = await api(`/api/v1/devices/${currentDevice.value.id}/tags/import/preview`, {
          method: 'POST', body: form
        });
        await ElMessageBox.confirm(
          `预检通过，共${preview.totalRows}个点位。载入后将替换当前页面中的点位列表，但不会立即修改草稿，是否继续？`,
          '设备点位导入预检',
          { type: 'warning', confirmButtonText: '载入点位', cancelButtonText: '取消' });
        tagList.value = preview.tags.map(tag => toTagEditorRow(tag, currentDevice.value.protocol));
        applyTagSamples(tagSamplesById.values());
        ElMessage.success('点位已载入编辑器，请核对并点击“保存设备参数与点位草稿”。');
      } catch (error) {
        const issues = error.data?.errors?.map(issue => `第${issue.row || '-'}行 ${issue.field}: ${issue.message}`).join('；');
        if (error !== 'cancel' && error !== 'close') ElMessage.error(issues || `设备点位导入失败：${error.message || error}`);
      } finally {
        isImportingTags.value = false;
        event.target.value = '';
      }
    };

    const exportDeviceTags = async () => {
      if (!currentDevice.value || isExportingTags.value) return;
      isExportingTags.value = true;
      try {
        const safeDeviceName = (currentDevice.value.name || 'Device').replace(/[\\/:*?"<>|]/g, '_');
        const timestamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '_').slice(0, 15);
        const suggestedName = `${safeDeviceName}_Points_${timestamp}.xlsx`;
        let fileHandle = null;
        if (typeof window.showSaveFilePicker === 'function') {
          fileHandle = await window.showSaveFilePicker({
            suggestedName,
            types: [{ description: 'EdgeNexus 设备点位表', accept: { 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': ['.xlsx'] } }]
          });
        }

        const response = await fetch(`/api/v1/devices/${currentDevice.value.id}/tags/export`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const blob = await response.blob();
        if (fileHandle) {
          const writable = await fileHandle.createWritable();
          try {
            await writable.write(blob);
          } finally {
            await writable.close();
          }
        } else {
          const url = URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = url;
          link.download = suggestedName;
          link.click();
          setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
        ElMessage.success('当前设备已保存的草稿点位已导出。');
      } catch (error) {
        if (error?.name !== 'AbortError') ElMessage.error(`设备点位导出失败：${error.message || error}`);
      } finally {
        isExportingTags.value = false;
      }
    };

    const saveTags = async () => {
      if (!currentDevice.value) return;
      isSavingTags.value = true;
      try {
        const group = groupList.value.find(item => item.children?.some(device => device.id === currentDevice.value.id));
        const updatedDevice = devicePayload(currentDevice.value, group.id);
        await api(`/api/v1/devices/${updatedDevice.id}`, { method: 'PUT', body: updatedDevice });
        const tags = tagList.value.map(tag => ({
          id: tag.id || crypto.randomUUID(),
          deviceId: currentDevice.value.id,
          name: tag.name?.trim(),
          address: tag.address?.trim(),
          dataType: tag.dataType,
          modbusFunctionCode: currentDevice.value.protocol === 'ModbusTcp' ? tag.functionCode : null,
          enabled: tag.enabled ?? true,
          byteOrder: tag.endian || 'ABCD',
          operator: tag.operator || '*',
          operand: Number(tag.operand ?? 1),
          decimals: Number(tag.decimals ?? 2),
          publishOnChange: Boolean(tag.onChange),
          deadband: Number(tag.deadband ?? 0),
          stringLength: Number(tag.stringLength ?? 32),
          remark: tag.remark || null
        }));
        await api(`/api/v1/devices/${currentDevice.value.id}/tags`, { method: 'PUT', body: tags });
        await loadConfiguration();
        drawerVisible.value = false;
        ElMessage.success('设备参数和点位已原子写入草稿。');
      } catch (error) {
        ElMessage.error(`点位保存失败：${error.message}`);
      } finally {
        isSavingTags.value = false;
      }
    };

    const mqttConfig = ref({
      enabled: false, gatewayId: 'EDGENEXUS_01', host: '127.0.0.1', port: 1883, clientId: 'EDGENEXUS_01',
      username: '', password: '', qos: 1, retain: false, useTls: false, lastWillTopic: 'edge/status',
      lastWillPayload: 'offline', storeAndForwardEnabled: true, outboxMaxMessages: 500000,
      maxPublishAttempts: 20, replayMaxMessagesPerSecond: 200
    });
    const showMqttTemplateModal = ref(false);
    const isEditingMqttTemplate = ref(false);
    const isSavingMqttTemplate = ref(false);
    const isTestingMqttTemplate = ref(false);
    const isPreviewingMqttTemplate = ref(false);
    const mqttTemplatePreview = ref(null);
    const mqttTemplateTestResult = ref(null);
    const mqttTemplateForm = ref(emptyMqttTemplate());
    const mqttTemplateVariables = ['deviceId', 'deviceName', 'deviceIp', 'protocol', 'gatewayId', 'gatewayVersion', 'timestamp', 'unixTime', 'groupName', 'line', 'tag', 'tagName', 'value', 'quality'];
    const mqttReservedFieldNames = new Set(['deviceid', 'devicename', 'deviceip', 'protocol', 'gatewayid', 'gatewayversion', 'timestamp', 'unixtime', 'ts', 'd', 'tag', 'value', 'quality']);
    const mqttTemplateSystemFields = [
      'includeDeviceId', 'includeDeviceName', 'includeDeviceIp', 'includeProtocol',
      'includeGatewayId', 'includeGatewayVersion', 'includeTimestamp', 'includeUnixTime',
      'includeTag', 'includeValue', 'includeQuality'
    ];
    const mqttTemplateTestData = ref({
      topicTemplate: 'factory/${deviceId}/data', deviceId: 'PLC001', deviceName: '一号PLC',
      deviceIp: '192.168.1.10', protocol: 'ModbusTcp', groupName: '一号线', line: 'LINE01',
      tagName: 'Temperature', value: 35.6, quality: 'Good', communicationState: 'normal'
    });
    let mqttPreviewTimer = null;
    let mqttPreviewSequence = 0;

    function emptyMqttTemplate() {
      const now = new Date().toISOString();
      return {
        id: '', name: '', enabled: true, isBuiltIn: false, payloadStyle: 'Advantech',
        includeDeviceId: false, includeDeviceName: false, includeDeviceIp: false, includeProtocol: false,
        includeGatewayId: false, includeGatewayVersion: false, includeTimestamp: true, includeUnixTime: false,
        includeTag: true, includeValue: true, includeQuality: true,
        deviceErrorTagPrefix: '#DEVICE_ERROR', communicationFailureCode: 1001, customFields: [],
        createTime: now, updateTime: now
      };
    }

    const enabledMqttTemplates = computed(() => mqttTemplates.value.filter(template => template.enabled));
    const mqttTemplateSelectedFieldCount = computed(() => mqttTemplateSystemFields.reduce(
      (count, field) => count + (mqttTemplateForm.value[field] ? 1 : 0), 0));
    const mqttTemplateHasDataField = computed(() => Boolean(
      mqttTemplateForm.value.includeTag || mqttTemplateForm.value.includeValue || mqttTemplateForm.value.includeQuality));
    const mqttTemplatePayloadBytes = computed(() => new TextEncoder().encode(mqttTemplatePreview.value?.prettyPayload || '').length);
    const mqttTemplateValidation = computed(() => {
      const errors = [];
      if (!mqttTemplateForm.value.name?.trim()) errors.push('模板名称不能为空。');
      if (!mqttTemplateHasDataField.value) errors.push('至少选择一个数据字段：tag、value 或 quality。');
      const statusTagPrefix = mqttTemplateForm.value.deviceErrorTagPrefix?.trim().replace(/_+$/, '') || '';
      if (!/^[#A-Za-z_][#A-Za-z0-9_.-]*$/.test(statusTagPrefix) || statusTagPrefix.length > 128) {
        errors.push('设备通讯状态 Tag 前缀格式无效。');
      }
      const failureCode = Number(mqttTemplateForm.value.communicationFailureCode);
      if (!Number.isInteger(failureCode) || failureCode === 0) {
        errors.push('通讯失败码必须是非0整数，可使用正数或负数。');
      }

      const names = new Set();
      for (const field of mqttTemplateForm.value.customFields || []) {
        const name = field.name?.trim() || '';
        if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) {
          errors.push(`自定义字段 ${name || '（未命名）'} 的名称无效。`);
          continue;
        }
        const normalizedName = name.toLowerCase();
        if (mqttReservedFieldNames.has(normalizedName) || names.has(normalizedName)) {
          errors.push(`自定义字段 ${name} 与系统字段冲突或重复。`);
          continue;
        }
        names.add(normalizedName);
      }

      return { errors };
    });

    const templatePayload = () => ({
      ...mqttTemplateForm.value,
      id: mqttTemplateForm.value.id || crypto.randomUUID(),
      name: mqttTemplateForm.value.name?.trim(),
      deviceErrorTagPrefix: mqttTemplateForm.value.deviceErrorTagPrefix?.trim().replace(/_+$/, ''),
      communicationFailureCode: Number(mqttTemplateForm.value.communicationFailureCode),
      customFields: (mqttTemplateForm.value.customFields || []).map(field => ({
        name: field.name?.trim(), value: String(field.value ?? '').trim()
      }))
    });

    const buildMqttTemplateRequest = includeTemplate => ({
      templateId: mqttTemplateForm.value.id,
      template: includeTemplate ? templatePayload() : null,
      topicTemplate: mqttTemplateTestData.value.topicTemplate,
      deviceId: mqttTemplateTestData.value.deviceId,
      deviceName: mqttTemplateTestData.value.deviceName,
      deviceIp: mqttTemplateTestData.value.deviceIp,
      protocol: mqttTemplateTestData.value.protocol,
      groupName: mqttTemplateTestData.value.groupName,
      line: mqttTemplateTestData.value.line,
      timestamp: new Date().toISOString(),
      communicationFailed: mqttTemplateTestData.value.communicationState === 'failed',
      tags: [{
        tagName: mqttTemplateTestData.value.tagName,
        value: mqttTemplateTestData.value.value,
        quality: mqttTemplateTestData.value.quality
      }]
    });

    const formatMqttTemplatePreview = result => {
      let prettyPayload = result.payload || '';
      try {
        prettyPayload = JSON.stringify(JSON.parse(result.payload), null, 2);
      } catch { }
      return { ...result, prettyPayload };
    };

    const refreshMqttTemplatePreview = async () => {
      if (!showMqttTemplateModal.value || !mqttTemplateForm.value.id) return;
      const sequence = ++mqttPreviewSequence;
      if (mqttTemplateValidation.value.errors.length > 0) {
        isPreviewingMqttTemplate.value = false;
        mqttTemplatePreview.value = {
          error: mqttTemplateValidation.value.errors[0],
          topic: '',
          prettyPayload: ''
        };
        return;
      }
      isPreviewingMqttTemplate.value = true;
      try {
        const result = await api('/api/v1/mqtt/templates/preview', {
          method: 'POST', body: buildMqttTemplateRequest(true)
        });
        if (sequence !== mqttPreviewSequence) return;
        mqttTemplatePreview.value = formatMqttTemplatePreview(result);
      } catch (error) {
        if (sequence !== mqttPreviewSequence) return;
        mqttTemplatePreview.value = { error: error.message, topic: '', prettyPayload: '' };
      } finally {
        if (sequence === mqttPreviewSequence) isPreviewingMqttTemplate.value = false;
      }
    };

    const scheduleMqttTemplatePreview = () => {
      if (mqttPreviewTimer) clearTimeout(mqttPreviewTimer);
      if (!showMqttTemplateModal.value) return;
      mqttPreviewTimer = setTimeout(refreshMqttTemplatePreview, 220);
    };

    watch([mqttTemplateForm, mqttTemplateTestData], scheduleMqttTemplatePreview, { deep: true });

    const openMqttTemplateModal = template => {
      isEditingMqttTemplate.value = Boolean(template?.id);
      mqttTemplateForm.value = template
        ? { ...emptyMqttTemplate(), ...JSON.parse(JSON.stringify(template)) }
        : { ...emptyMqttTemplate(), id: crypto.randomUUID(), name: '新MQTT模板' };
      mqttTemplatePreview.value = null;
      mqttTemplateTestResult.value = null;
      showMqttTemplateModal.value = true;
      scheduleMqttTemplatePreview();
    };

    const editMqttTemplate = template => {
      if (!template?.id) {
        ElMessage.warning('未找到要编辑的MQTT模板。');
        return;
      }

      openMqttTemplateModal(template);
    };

    const closeMqttTemplateModal = () => {
      if (mqttPreviewTimer) clearTimeout(mqttPreviewTimer);
      mqttPreviewTimer = null;
      mqttPreviewSequence++;
      isPreviewingMqttTemplate.value = false;
      mqttTemplateTestResult.value = null;
    };

    const setMqttFieldGroup = (fields, enabled) => {
      for (const field of fields) mqttTemplateForm.value[field] = enabled;
    };

    const addMqttCustomField = () => {
      if (mqttTemplateForm.value.customFields.length >= 64) {
        ElMessage.warning('单个模板最多允许64个自定义字段。');
        return;
      }
      mqttTemplateForm.value.customFields.push({ name: '', value: '' });
    };

    const appendMqttCustomFieldVariable = (index, variable) => {
      const field = mqttTemplateForm.value.customFields[index];
      if (!field) return;
      const token = '${' + variable + '}';
      field.value = `${field.value || ''}${token}`;
    };

    const copyMqttTemplatePreview = async target => {
      const value = target === 'topic' ? mqttTemplatePreview.value?.topic : mqttTemplatePreview.value?.prettyPayload;
      if (!value) return;
      try {
        await navigator.clipboard.writeText(value);
        ElMessage.success(target === 'topic' ? 'Topic 已复制。' : 'JSON 报文已复制。');
      } catch {
        ElMessage.error('复制失败，请检查浏览器剪贴板权限。');
      }
    };

    const saveMqttTemplate = async (closeAfterSave = true, showMessage = true) => {
      if (mqttTemplateValidation.value.errors.length > 0) {
        ElMessage.warning(mqttTemplateValidation.value.errors[0]);
        return null;
      }

      isSavingMqttTemplate.value = true;
      try {
        const payload = templatePayload();
        const saved = await api(isEditingMqttTemplate.value ? `/api/v1/mqtt/templates/${payload.id}` : '/api/v1/mqtt/templates', {
          method: isEditingMqttTemplate.value ? 'PUT' : 'POST', body: payload
        });
        mqttTemplateForm.value = JSON.parse(JSON.stringify(saved));
        isEditingMqttTemplate.value = true;
        mqttTemplates.value = await api('/api/v1/mqtt/templates');
        if (closeAfterSave) showMqttTemplateModal.value = false;
        if (showMessage) ElMessage.success('MQTT模板已热更新，无需重启网关。');
        return saved;
      } catch (error) {
        ElMessage.error(`MQTT模板保存失败：${error.message}`);
        return null;
      } finally {
        isSavingMqttTemplate.value = false;
      }
    };

    const toggleMqttTemplate = async template => {
      try {
        await api(`/api/v1/mqtt/templates/${template.id}`, { method: 'PUT', body: template });
        ElMessage.success(template.enabled ? '模板已启用并热更新。' : '模板已停用，已绑定设备将回退到默认模板。');
      } catch (error) {
        template.enabled = !template.enabled;
        ElMessage.error(`模板状态更新失败：${error.message}`);
      }
    };

    const deleteMqttTemplate = template => {
      ElMessageBox.confirm(`确定删除MQTT模板 [${template.name}] 吗？`, '删除模板', { type: 'warning' })
        .then(async () => {
          await api(`/api/v1/mqtt/templates/${template.id}`, { method: 'DELETE' });
          mqttTemplates.value = await api('/api/v1/mqtt/templates');
          ElMessage.success('MQTT模板已删除。');
        })
        .catch(error => { if (error instanceof Error) ElMessage.error(error.message); });
    };

    const testSendMqttTemplate = async () => {
      mqttTemplateTestResult.value = null;
      const saved = await saveMqttTemplate(false, false);
      if (!saved) return;
      isTestingMqttTemplate.value = true;
      try {
        const result = await api('/api/v1/mqtt/templates/test-send', {
          method: 'POST', body: buildMqttTemplateRequest(false)
        });
        mqttTemplatePreview.value = formatMqttTemplatePreview(result);
        mqttTemplateTestResult.value = {
          state: 'success',
          message: `Topic：${result.topic}`,
          time: new Date().toLocaleTimeString('zh-CN', { hour12: false })
        };
        ElMessage.success('测试报文已进入异步发送队列。');
      } catch (error) {
        mqttTemplateTestResult.value = {
          state: 'error',
          message: error.message,
          time: new Date().toLocaleTimeString('zh-CN', { hour12: false })
        };
        ElMessage.error(`测试发送失败：${error.message}`);
      } finally {
        isTestingMqttTemplate.value = false;
      }
    };

    const settings = ref({ memLimit: 2048 });
    const isExporting = ref(false);
    const showVersionManager = ref(false);
    const revisionList = ref([]);
    const selectedRevision = ref(null);
    const revisionPreview = ref(null);
    const isLoadingVersions = ref(false);
    const isLoadingRevisionPreview = ref(false);
    const isRollingBack = ref(false);
    const isDeletingRevision = ref(false);
    let revisionPreviewSequence = 0;

    const saveMqtt = async (showMessage = true) => {
      try {
        mqttConfig.value.port = Number(mqttConfig.value.port);
        await api('/api/v1/mqtt/config', { method: 'PUT', body: mqttConfig.value });
        if (showMessage) ElMessage.success('MQTT 参数已保存到配置草稿，发布后生效。');
        return true;
      } catch (error) {
        ElMessage.error(`MQTT 保存失败：${error.message}`);
        return false;
      }
    };

    const mqttRuntimeStateLabel = computed(() => ({
      Disabled: '已停用',
      Connecting: '连接中',
      Connected: '已连接',
      Reconnecting: '重连中',
      Faulted: '连接失败 · 自动重连'
    })[mqttRuntime.value.state] || '状态未知');

    const mqttRuntimeTagType = computed(() => ({
      Disabled: 'info',
      Connecting: 'warning',
      Connected: 'success',
      Reconnecting: 'warning',
      Faulted: 'danger'
    })[mqttRuntime.value.state] || 'info');

    const formatMqttRuntimeTime = value => value
      ? new Intl.DateTimeFormat('zh-CN', {
        hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false
      }).format(new Date(value))
      : '—';

    const applyMqtt = async () => {
      isApplyingMqtt.value = true;
      try {
        mqttConfig.value.port = Number(mqttConfig.value.port);
        const result = await api('/api/v1/mqtt/config/apply', { method: 'POST', body: mqttConfig.value });
        await refreshMqttRuntime();
        ElMessage.success(mqttConfig.value.enabled
          ? `MQTT 配置已作为 V${result.version} 独立应用，正在连接 Broker。`
          : `MQTT 配置已作为 V${result.version} 独立应用并停用。`);
      } catch (error) {
        ElMessage.error(`MQTT 应用失败：${error.message}`);
      } finally {
        isApplyingMqtt.value = false;
      }
    };

    const validateDraft = async (showMessage = true) => {
      try {
        const result = await api('/api/v1/configuration/validate', { method: 'POST' });
        if (!result.isValid) {
          if (showMessage) ElMessage.error(result.errors.join('；'));
          return result;
        }
        if (showMessage) ElMessage.success(result.warnings.length ? `校验通过，警告：${result.warnings.join('；')}` : '配置草稿校验通过。');
        return result;
      } catch (error) {
        ElMessage.error(`配置校验失败：${error.message}`);
        return null;
      }
    };

    const publishDraft = async () => {
      const validation = await validateDraft();
      if (!validation?.isValid) return;
      try {
        const { value } = await ElMessageBox.prompt('请输入本次发布说明，便于版本审计与回滚。', '发布配置', {
          confirmButtonText: '确认发布', cancelButtonText: '取消', inputValue: 'Web控制台发布', inputPattern: /\S+/, inputErrorMessage: '发布说明不能为空'
        });
        const result = await api('/api/v1/configuration/publish', { method: 'POST', body: { reason: value } });
        ElMessage.success(`配置版本 V${result.version} 已发布并热更新。`);
        await loadConfiguration();
      } catch (error) {
        if (error instanceof Error) ElMessage.error(`发布失败：${error.message}`);
      }
    };

    const formatRevisionTime = value => {
      if (!value) return '—';
      return new Intl.DateTimeFormat('zh-CN', {
        year: 'numeric', month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false
      }).format(new Date(value));
    };

    const selectRevision = async revision => {
      if (!revision) return;
      selectedRevision.value = revision;
      revisionPreview.value = null;
      const requestSequence = ++revisionPreviewSequence;
      isLoadingRevisionPreview.value = true;
      try {
        const preview = await api(`/api/v1/configuration/revisions/${revision.version}/preview`);
        if (requestSequence === revisionPreviewSequence) revisionPreview.value = preview;
      } catch (error) {
        if (requestSequence === revisionPreviewSequence) {
          ElMessage.error(`版本预检失败：${error.message}`);
        }
      } finally {
        if (requestSequence === revisionPreviewSequence) isLoadingRevisionPreview.value = false;
      }
    };

    const loadRevisions = async () => {
      if (isLoadingVersions.value) return;
      isLoadingVersions.value = true;
      try {
        const revisions = await api('/api/v1/configuration/revisions?take=100');
        revisionList.value = revisions;
        const previousVersion = selectedRevision.value?.version;
        const preferred = revisions.find(item => item.version === previousVersion)
          || revisions.find(item => !item.isActive)
          || revisions[0]
          || null;
        if (preferred) await selectRevision(preferred);
        else {
          selectedRevision.value = null;
          revisionPreview.value = null;
        }
      } catch (error) {
        ElMessage.error(`版本列表加载失败：${error.message}`);
      } finally {
        isLoadingVersions.value = false;
      }
    };

    const openVersionManager = async () => {
      showVersionManager.value = true;
      await loadRevisions();
    };

    const rollbackSelectedRevision = async () => {
      const revision = selectedRevision.value;
      const preview = revisionPreview.value;
      if (!revision || !preview || revision.isActive || !preview.validation.isValid) return;
      try {
        await ElMessageBox.confirm(
          `即将把完整工程配置从 V${preview.activeVersion} 回滚到 V${revision.version}。回滚会生成新的审计版本，是否继续？`,
          '确认工程回滚',
          { confirmButtonText: '继续回滚', cancelButtonText: '取消', type: 'warning' }
        );
        const { value: reason } = await ElMessageBox.prompt(
          '请输入回滚原因，内容将写入版本审计记录。',
          `回滚到 V${revision.version}`,
          {
            confirmButtonText: '确认执行', cancelButtonText: '取消',
            inputValue: `回滚到 V${revision.version}`,
            inputPattern: /\S+/,
            inputErrorMessage: '回滚原因不能为空'
          }
        );
        isRollingBack.value = true;
        const result = await api(`/api/v1/configuration/rollback/${revision.version}`, {
          method: 'POST',
          body: { reason, expectedActiveVersion: preview.activeVersion }
        });
        ElMessage.success(`回滚成功，已生成并热更新为 V${result.version}。`);
        selectedRevision.value = null;
        await Promise.all([loadConfiguration(), loadRevisions()]);
      } catch (error) {
        if (error instanceof Error) ElMessage.error(`工程回滚失败：${error.message}`);
      } finally {
        isRollingBack.value = false;
      }
    };

    const deleteSelectedRevision = async () => {
      const revision = selectedRevision.value;
      const preview = revisionPreview.value;
      if (!revision || !preview || revision.isActive) return;
      try {
        await ElMessageBox.confirm(
          `确定删除历史版本 V${revision.version}？该版本将从列表和回滚入口隐藏，不影响当前运行版本 V${preview.activeVersion}。`,
          '删除历史版本',
          { confirmButtonText: '继续删除', cancelButtonText: '取消', type: 'error' }
        );
        const { value: reason } = await ElMessageBox.prompt(
          '请输入删除原因。系统会保留不可变的删除审计记录。',
          `删除 V${revision.version}`,
          {
            confirmButtonText: '确认删除', cancelButtonText: '取消',
            inputValue: `清理历史版本 V${revision.version}`,
            inputPattern: /\S+/,
            inputErrorMessage: '删除原因不能为空'
          }
        );
        isDeletingRevision.value = true;
        await api(`/api/v1/configuration/revisions/${revision.version}/delete`, {
          method: 'POST',
          body: { reason, expectedActiveVersion: preview.activeVersion }
        });
        ElMessage.success(`历史版本 V${revision.version} 已删除，当前运行配置未改变。`);
        selectedRevision.value = null;
        revisionPreview.value = null;
        await loadRevisions();
      } catch (error) {
        if (error instanceof Error) ElMessage.error(`版本删除失败：${error.message}`);
      } finally {
        isDeletingRevision.value = false;
      }
    };

    const triggerProjectImport = () => document.getElementById('project-upload').click();

    const handleImport = async event => {
      const file = event.target.files?.[0];
      if (!file) return;
      const loading = ElLoading.service({ lock: true, text: '正在预检点表...', background: 'rgba(0,0,0,.8)' });
      try {
        const previewForm = new FormData();
        previewForm.append('file', file);
        const preview = await api('/api/v1/configuration/import/preview?replaceExisting=false', { method: 'POST', body: previewForm });
        await ElMessageBox.confirm(
          `预检通过：${preview.groupCount}组、${preview.deviceCount}台设备、${preview.tagCount}个点位。确认合并到配置草稿？`,
          '点表预检通过', { type: 'success' });
        loading.setText('正在原子写入配置草稿...');
        const importForm = new FormData();
        importForm.append('file', file);
        await api('/api/v1/configuration/import?replaceExisting=false', { method: 'POST', body: importForm });
        await loadConfiguration();
        ElMessage.success('点表已导入草稿，校验并发布后生效。');
      } catch (error) {
        const issues = error.data?.errors?.map(issue => `第${issue.row || '-'}行 ${issue.field}: ${issue.message}`).join('；');
        if (error !== 'cancel' && error !== 'close') ElMessage.error(issues || `点表导入失败：${error.message || error}`);
      } finally {
        loading.close();
        event.target.value = '';
      }
    };

    const exportProject = async () => {
      if (isExporting.value) return;
      isExporting.value = true;
      try {
        const timestamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '_').slice(0, 15);
        const suggestedName = `EdgeNexus_Draft_${timestamp}.json`;
        let fileHandle = null;
        let fallbackName = suggestedName;
        if (typeof window.showSaveFilePicker === 'function') {
          fileHandle = await window.showSaveFilePicker({
            suggestedName,
            types: [{ description: 'EdgeNexus 工程配置', accept: { 'application/json': ['.json'] } }]
          });
        } else {
          const result = await ElMessageBox.prompt('请输入导出文件名', '导出工程', {
            confirmButtonText: '导出', cancelButtonText: '取消', inputValue: suggestedName,
            inputPattern: /^(?![. ])[^\\/:*?"<>|]+\.json$/i,
            inputErrorMessage: '请输入有效的 .json 文件名'
          });
          fallbackName = result.value;
        }

        const response = await fetch('/api/v1/configuration/export', { headers: { Accept: 'application/json' } });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const blob = await response.blob();
        if (fileHandle) {
          const writable = await fileHandle.createWritable();
          try {
            await writable.write(blob);
          } finally {
            await writable.close();
          }
        } else {
          const url = URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = url;
          link.download = fallbackName;
          link.click();
          setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
        ElMessage.success('配置草稿已导出。');
      } catch (error) {
        if (error?.name !== 'AbortError' && error !== 'cancel' && error !== 'close') ElMessage.error(`导出失败：${error.message || error}`);
      } finally {
        isExporting.value = false;
      }
    };

    const bindableNetworkInterfaces = computed(() => networkInterfaces.value.filter(item => item.isBindable));
    const networkDeviceOptions = computed(() => groupList.value.flatMap(group =>
      (group.children || []).map(device => ({ id: device.id, label: `${group.name} / ${device.name}`, protocol: device.protocol }))));
    const usageLabel = usage => ({
      PlcAcquisition: 'PLC采集网络', MqttUpload: 'MQTT上传网络', WebManagement: 'Web管理网络'
    }[usage] || usage);
    const effectiveBinding = usage => networkEffectiveBindings.value.find(item => item.usage === usage) || null;
    const formatNetworkAddresses = addresses => (addresses || []).map(item => `${item.address} / ${item.subnetMask || '-'}`).join('\n') || '-';
    const formatNetworkList = values => (values || []).join('\n') || '-';
    const dhcpLabel = value => value === true ? 'DHCP' : value === false ? '静态' : '未知';

    const refreshNetworkConfiguration = async () => {
      if (isRefreshingNetwork.value) return;
      isRefreshingNetwork.value = true;
      try {
        applyNetworkConfiguration(await api('/api/v1/network/configuration?refresh=true'));
        ElMessage.success('已重新读取操作系统网卡信息。');
      } catch (error) {
        ElMessage.error(`网卡刷新失败：${error.message}`);
      } finally {
        isRefreshingNetwork.value = false;
      }
    };

    const saveNetworkConfiguration = async () => {
      if (isSavingNetwork.value) return;
      isSavingNetwork.value = true;
      try {
        const result = await api('/api/v1/network/configuration', { method: 'PUT', body: networkBindings.value });
        applyNetworkConfiguration(result);
        ElMessage.success('网络用途绑定已保存并应用；未修改操作系统IP配置。');
      } catch (error) {
        ElMessage.error(`网络用途绑定保存失败：${error.message}`);
      } finally {
        isSavingNetwork.value = false;
      }
    };

    const runNetworkDiagnostic = async () => {
      if (isRunningNetworkDiagnostic.value) return;
      const type = networkDiagnosticType.value;
      const form = networkDiagnosticForm.value;
      if ((type === 'ping' || type === 'tcp') && !form.host?.trim()) return ElMessage.warning('请输入目标主机。');
      if (type === 'tcp' && (!Number(form.port) || Number(form.port) > 65535)) return ElMessage.warning('请输入有效TCP端口。');
      if (type === 'plc' && !form.deviceId) return ElMessage.warning('请选择PLC设备。');
      isRunningNetworkDiagnostic.value = true;
      networkDiagnosticResult.value = null;
      try {
        const request = type === 'ping'
          ? { path: '/api/v1/network/diagnostics/ping', body: { host: form.host.trim(), usage: form.usage, timeoutMs: Number(form.timeoutMs) } }
          : type === 'tcp'
            ? { path: '/api/v1/network/diagnostics/tcp', body: { host: form.host.trim(), port: Number(form.port), usage: form.usage, timeoutMs: Number(form.timeoutMs) } }
            : type === 'mqtt'
              ? { path: '/api/v1/network/diagnostics/mqtt', body: { timeoutMs: Number(form.timeoutMs) } }
              : { path: '/api/v1/network/diagnostics/plc', body: { deviceId: form.deviceId, timeoutMs: Number(form.timeoutMs) } };
        networkDiagnosticResult.value = await api(request.path, { method: 'POST', body: request.body });
        const message = networkDiagnosticResult.value.success ? '诊断成功，链路可用。' : `诊断失败：${networkDiagnosticResult.value.error || networkDiagnosticResult.value.status}`;
        networkDiagnosticResult.value.success ? ElMessage.success(message) : ElMessage.error(message);
      } catch (error) {
        networkDiagnosticResult.value = { success: false, status: 'RequestFailed', durationMs: 0, error: error.message };
        ElMessage.error(`网络诊断失败：${error.message}`);
      } finally {
        isRunningNetworkDiagnostic.value = false;
      }
    };

    onMounted(() => { void restoreSession(); });
    onUnmounted(() => {
      if (runtimeTimer) clearInterval(runtimeTimer);
      if (mqttRuntimeTimer) clearInterval(mqttRuntimeTimer);
      if (mqttPreviewTimer) clearTimeout(mqttPreviewTimer);
      void stopRealtime();
    });

    return {
      isLoggedIn, isLoggingIn, loginForm, currentRole, handleLogin, handleUserCommand, showPwdModal, pwdForm, savePassword,
      activeNav, isCollecting, toggleAcquisition, groupList, activeDashboardGroups, totalDevices, onlineDevices,
      offlineDevices, deviceStatusType, deviceStatusLabel, deviceHealthType, deviceHealthLabel,
      resource, realtimeConnected, protocolOptions, refreshRuntime, batchCopyDevice,
      showAddGroupModal, isEditGroup, newGroup, openAddGroupModal, saveGroup, deleteGroup,
      showDeviceModal, isEditDevice, isTestingConnection, newDevice, openAddDeviceModal, openEditDeviceModal,
      applyDefaultPort, selectedProtocolFields, saveDevice, testConnection, deleteDevice,
      shouldShowProtocolField, siemensRecommendation, connectionDiagnostic,
      drawerVisible, currentDevice, currentDeviceProtocolFields, currentPointAddressGuide, currentProtocolLabel, tagList,
      isSavingTags, isImportingTags, isExportingTags, openTagDrawer, closeTagDrawer,
      addPoint, addPointFromExample, calcFinalVal, qualityLabel, saveTags,
      triggerDeviceTagImport, handleDeviceTagImport, exportDeviceTags,
      mqttConfig, mqttRuntime, mqttRuntimeStateLabel, mqttRuntimeTagType, formatMqttRuntimeTime,
      isApplyingMqtt, settings, saveMqtt, applyMqtt, validateDraft, publishDraft, isExporting,
      mqttTemplates, enabledMqttTemplates, showMqttTemplateModal, isEditingMqttTemplate,
      mqttTemplateForm, mqttTemplateTestData, mqttTemplatePreview, mqttTemplateTestResult,
      mqttTemplateVariables, mqttTemplateSelectedFieldCount, mqttTemplateHasDataField, mqttTemplatePayloadBytes, mqttTemplateValidation,
      isSavingMqttTemplate, isTestingMqttTemplate, isPreviewingMqttTemplate,
      openMqttTemplateModal, editMqttTemplate, closeMqttTemplateModal, setMqttFieldGroup, addMqttCustomField, appendMqttCustomFieldVariable,
      copyMqttTemplatePreview, saveMqttTemplate, toggleMqttTemplate, deleteMqttTemplate, testSendMqttTemplate,
      showVersionManager, revisionList, selectedRevision, revisionPreview,
      isLoadingVersions, isLoadingRevisionPreview, isRollingBack, isDeletingRevision,
      openVersionManager, loadRevisions, selectRevision, rollbackSelectedRevision, deleteSelectedRevision, formatRevisionTime,
      triggerProjectImport, handleImport, exportProject,
      networkInterfaces, bindableNetworkInterfaces, networkBindings, networkEffectiveBindings, networkDeviceOptions,
      isRefreshingNetwork, isSavingNetwork, refreshNetworkConfiguration, saveNetworkConfiguration,
      networkDiagnosticType, networkDiagnosticForm, networkDiagnosticResult, isRunningNetworkDiagnostic, runNetworkDiagnostic,
      usageLabel, effectiveBinding, formatNetworkAddresses, formatNetworkList, dhcpLabel
    };
  }
});

app.use(ElementPlus);
for (const [key, component] of Object.entries(ElementPlusIconsVue)) app.component(key, component);
app.mount('#app');
