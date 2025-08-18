var VoiceRecognizerPlugin = {
    $VoiceRecognizer: {
        webSocket: null,
        mediaRecorder: null,
        audioContext: null,
        audioStream: null,
        isRecording: false,
        gameObjectName: "GameApp",
        websocketUrl: null,

        // 统一服务模式支持
        useUnifiedService: true,           // 是否使用统一服务模式
        unifiedServiceConnected: false,    // 统一服务连接状态
        conversationActive: false,         // 对话是否激活

        // 会话状态管理（兼容传统模式）
        sessionActive: false,
        permissionGranted: false,
        connectionReady: false,

        // 音频发送控制
        isSendingAudio: false,        // 是否正在发送音频
        lastVolumeLevel: 0,           // 最后音量级别
        silenceStartTime: 0,          // 静音开始时间
        lastResultTime: 0,            // 最后识别结果时间
        recordingStartTime: 0,        // 录音开始时间

        // 配置参数
        silenceThreshold: 0.02,       // 静音阈值
        silenceTimeout: 1500,         // 静音超时(ms)
        resultTimeout: 5000,          // 结果超时(ms)
        maxRecordingTime: 30000,      // 最大录音时间(ms)
        
        // 🔧 新增：自适应静音检测参数
        backgroundNoiseLevel: 0,      // 背景噪音水平
        noiseCalibrationSamples: 0,   // 噪音校准样本数
        maxNoiseCalibrationSamples: 100, // 最大校准样本数
        adaptiveThresholdMultiplier: 2.5, // 自适应阈值倍数

        // 音频处理相关
        sampleRate: 16000,
        bufferSize: 4096,
        audioBuffer: [],

        // 定时器
        resultTimeoutInterval: null,

        // 重采样相关
        needsResampling: false,
        actualSampleRate: 0,

        // 流式识别去重
        lastRecognitionText: "",

        // 统一服务专用WebSocket
        unifiedWebSocket: null,
        unifiedGameObjectName: "",
        unifiedConnectionStatus: false,
        
        // 🔧 连接恢复机制相关状态
        connectionAttempts: 0,
        maxReconnectAttempts: 5,
        reconnectDelay: 1000,         // 基础重连延迟 1秒
        maxReconnectDelay: 30000,     // 最大重连延迟 30秒
        reconnectTimer: null,
        lastConnectionUrl: '',
        isReconnecting: false,
        heartbeatInterval: null,
        heartbeatDelay: 30000,        // 心跳间隔 30秒
        lastHeartbeatTime: 0,
        connectionHealthCheck: null,
        

        // 初始化WebSocket连接（支持统一服务）
        initWebSocket: function(url) {
            try {
                VoiceRecognizer.websocketUrl = url;
                VoiceRecognizer.webSocket = new WebSocket(url);

                // 设置二进制数据类型为ArrayBuffer
                VoiceRecognizer.webSocket.binaryType = 'arraybuffer';

                VoiceRecognizer.webSocket.onopen = function(event) {
                    console.log('🔗 WebGL WebSocket连接成功 - 模式:', VoiceRecognizer.useUnifiedService ? '统一服务' : '传统');
                    VoiceRecognizer.connectionReady = true;

                    if (VoiceRecognizer.useUnifiedService) {
                        VoiceRecognizer.unifiedServiceConnected = true;
                        SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceConnected', '');
                        
                        // 🔧 统一服务连接成功后自动请求麦克风权限
                        console.log('🎤 统一服务连接成功，自动请求麦克风权限');
                        VoiceRecognizer.requestMicrophonePermission();
                    } else {
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'connected');
                        // 传统模式：连接成功后，检查是否可以开始会话
                    VoiceRecognizer.checkAndStartSession();
                    }
                };

                VoiceRecognizer.webSocket.onmessage = function(event) {
                    if (VoiceRecognizer.useUnifiedService) {
                        VoiceRecognizer.handleUnifiedServiceMessage(event);
                    } else {
                    console.log('WebGL收到消息:', event.data);
                    VoiceRecognizer.handleServerMessage(event.data);
                    }
                };

                VoiceRecognizer.webSocket.onclose = function(event) {
                    console.log('🔌 WebGL WebSocket连接关闭');
                    VoiceRecognizer.connectionReady = false;
                    VoiceRecognizer.sessionActive = false;
                    VoiceRecognizer.unifiedServiceConnected = false;
                    VoiceRecognizer.conversationActive = false;

                    if (VoiceRecognizer.useUnifiedService) {
                        SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceDisconnected', '');
                    } else {
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'disconnected');
                    }
                };

                VoiceRecognizer.webSocket.onerror = function(error) {
                    console.error('❌ WebGL WebSocket错误:', error);
                    VoiceRecognizer.connectionReady = false;
                    VoiceRecognizer.unifiedServiceConnected = false;

                    if (VoiceRecognizer.useUnifiedService) {
                        SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceError', 'WebSocket连接错误');
                    } else {
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLError', 'WebSocket连接错误');
                    }
                };

            } catch (error) {
                console.error('❌ WebSocket初始化失败:', error);
                var errorMessage = 'WebSocket初始化失败: ' + error.message;
                if (VoiceRecognizer.useUnifiedService) {
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceError', errorMessage);
                } else {
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLError', errorMessage);
                }
            }
        },
        
        // 请求麦克风权限（延迟调用）
        requestMicrophonePermission: function() {
            // 添加检查，防止重复初始化
            if (VoiceRecognizer.permissionGranted && VoiceRecognizer.audioContext) {
                console.log('⚠️ 麦克风权限已获取且音频处理已设置，跳过重复初始化');
                return;
            }

            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLError', '浏览器不支持麦克风访问');
                return;
            }

            console.log('请求麦克风权限...');
            SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'requesting_permission');

            navigator.mediaDevices.getUserMedia({
                audio: {
                    sampleRate: VoiceRecognizer.sampleRate,
                    channelCount: 1,
                    echoCancellation: true,
                    noiseSuppression: true
                }
            })
            .then(function(stream) {
                console.log('麦克风权限获取成功');
                VoiceRecognizer.audioStream = stream;
                VoiceRecognizer.permissionGranted = true;
                VoiceRecognizer.setupAudioProcessing(stream);

                // 🔧 修复：权限状态应该发送到VoiceRecognizerModel
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'permission_granted');

                // 权限获取成功后，检查是否可以开始会话
                VoiceRecognizer.checkAndStartSession();
            })
            .catch(function(error) {
                console.error('麦克风权限被拒绝:', error);
                VoiceRecognizer.permissionGranted = false;
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLError', '麦克风权限被拒绝: ' + error.message);
            });
        },
        
        // 设置音频处理
        setupAudioProcessing: function(stream) {
            try {
                // 尝试创建指定采样率的AudioContext
                VoiceRecognizer.audioContext = new (window.AudioContext || window.webkitAudioContext)({
                    sampleRate: VoiceRecognizer.sampleRate
                });

                // 验证实际采样率
                VoiceRecognizer.actualSampleRate = VoiceRecognizer.audioContext.sampleRate;
                VoiceRecognizer.needsResampling = VoiceRecognizer.actualSampleRate !== VoiceRecognizer.sampleRate;

                console.log('=== 音频配置验证 ===');
                console.log('期望采样率:', VoiceRecognizer.sampleRate);
                console.log('实际采样率:', VoiceRecognizer.actualSampleRate);
                console.log('需要重采样:', VoiceRecognizer.needsResampling);
                console.log('重采样比率:', VoiceRecognizer.needsResampling ? (VoiceRecognizer.actualSampleRate / VoiceRecognizer.sampleRate).toFixed(3) : 'N/A');
                console.log('===================');

                if (VoiceRecognizer.needsResampling) {
                    console.warn('警告：浏览器不支持16kHz采样率，将使用重采样处理');
                }

                var source = VoiceRecognizer.audioContext.createMediaStreamSource(stream);
                var processor = VoiceRecognizer.audioContext.createScriptProcessor(VoiceRecognizer.bufferSize, 1, 1);

                processor.onaudioprocess = function(event) {
                    //console.log('🎵 onaudioprocess回调触发', Date.now());
                    // 🔧 兼容统一服务和传统模式的WebSocket检查
                    var activeWebSocket = VoiceRecognizer.useUnifiedService ? VoiceRecognizer.unifiedWebSocket : VoiceRecognizer.webSocket;
                    var webSocketReady = activeWebSocket && activeWebSocket.readyState === WebSocket.OPEN;
                    
                    // 📊 定期输出音频处理状态（每100次输出一次，避免日志过多）
                    if (!VoiceRecognizer._audioProcessCounter) VoiceRecognizer._audioProcessCounter = 0;
                    VoiceRecognizer._audioProcessCounter++;
                    
                    if (VoiceRecognizer._audioProcessCounter % 100 === 1) {
                        console.log('📊 音频处理状态检查:', {
                            isRecording: VoiceRecognizer.isRecording,
                            webSocketReady: webSocketReady,
                            conversationActive: VoiceRecognizer.conversationActive,
                            useUnifiedService: VoiceRecognizer.useUnifiedService,
                            unifiedConnectionStatus: VoiceRecognizer.unifiedConnectionStatus
                        });
                    }

                    //console.log("VoiceRecognizer.isRecording:",VoiceRecognizer.isRecording)
                    //console.log("webSocketReady:",webSocketReady)

                    if (VoiceRecognizer.isRecording && webSocketReady) {
                        var inputBuffer = event.inputBuffer.getChannelData(0);

                        // 计算当前音频缓冲区的音量
                        var volume = VoiceRecognizer.calculateVolume(inputBuffer);
                        var isSendingAudio = VoiceRecognizer.shouldSendAudio(volume);
                        //console.log("isSendingAudio:",isSendingAudio)
                        // 检查是否应该发送音频数据
                        if (isSendingAudio) {
                            VoiceRecognizer.processAudioData(inputBuffer);
                        } else {
                            // 不发送音频，但继续监听
                            if (VoiceRecognizer._audioProcessCounter % 200 === 1) {
                                console.log('跳过音频发送 - 静音或超时，音量:', volume.toFixed(4));
                            }
                        }
                    }
                };

                source.connect(processor);
                // 🔧 正确修复：使用静音GainNode确保音频处理器正常工作
                var gainNode = VoiceRecognizer.audioContext.createGain();
                gainNode.gain.value = 0; // 静音处理，避免音频回放
                processor.connect(gainNode);
                gainNode.connect(VoiceRecognizer.audioContext.destination);

                console.log('WebGL音频处理设置完成');
                console.log('缓冲区大小:', VoiceRecognizer.bufferSize);
                console.log('每包音频时长(ms):', (VoiceRecognizer.bufferSize / VoiceRecognizer.actualSampleRate * 1000).toFixed(2));
                console.log('音频处理器已连接');
                
            } catch (error) {
                console.error('音频处理设置失败:', error);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLError', '音频处理设置失败: ' + error.message);
            }
        },
        
        // 重采样音频数据到16kHz
        resampleTo16k: function(inputBuffer) {
            if (!VoiceRecognizer.needsResampling) {
                return inputBuffer;
            }

            var inputSampleRate = VoiceRecognizer.actualSampleRate;
            var outputSampleRate = VoiceRecognizer.sampleRate;
            var ratio = inputSampleRate / outputSampleRate;
            var outputLength = Math.round(inputBuffer.length / ratio);
            var outputBuffer = new Float32Array(outputLength);

            console.log('重采样：', inputBuffer.length, '→', outputLength, '样本，比率:', ratio.toFixed(3));

            // 简单的线性插值重采样
            for (var i = 0; i < outputLength; i++) {
                var srcIndex = i * ratio;
                var index = Math.floor(srcIndex);
                var fraction = srcIndex - index;

                if (index + 1 < inputBuffer.length) {
                    // 线性插值
                    outputBuffer[i] = inputBuffer[index] * (1 - fraction) +
                                     inputBuffer[index + 1] * fraction;
                } else {
                    // 边界处理
                    outputBuffer[i] = inputBuffer[index] || 0;
                }
            }

            return outputBuffer;
        },

        // 转换Float32为16位PCM
        convertToInt16PCM: function(float32Array) {
            var int16Array = new Int16Array(float32Array.length);
            for (var i = 0; i < float32Array.length; i++) {
                // 限制范围到[-1, 1]
                var sample = Math.max(-1, Math.min(1, float32Array[i]));
                // 转换为16位整数
                int16Array[i] = sample < 0 ? sample * 0x8000 : sample * 0x7FFF;
            }
            return int16Array;
        },

        // 计算音频缓冲区的音量级别
        calculateVolume: function(inputBuffer) {
            var sum = 0;
            var maxSample = 0;
            
            // 🔧 优化：计算RMS和峰值，提供更准确的音量检测
            for (var i = 0; i < inputBuffer.length; i++) {
                var sample = Math.abs(inputBuffer[i]);
                sum += inputBuffer[i] * inputBuffer[i];
                if (sample > maxSample) {
                    maxSample = sample;
                }
            }
            
            var rms = Math.sqrt(sum / inputBuffer.length);
            
            // 🔧 使用RMS和峰值的组合来提供更稳定的音量检测
            // 如果峰值很高但RMS较低，可能是突发噪音，降低权重
            var combinedVolume = rms * 0.7 + maxSample * 0.3;
            
            // 🔧 添加音量平滑处理，减少突变
            if (!VoiceRecognizer._smoothedVolume) {
                VoiceRecognizer._smoothedVolume = combinedVolume;
            } else {
                var smoothingFactor = 0.3; // 平滑系数
                VoiceRecognizer._smoothedVolume = 
                    VoiceRecognizer._smoothedVolume * (1 - smoothingFactor) + 
                    combinedVolume * smoothingFactor;
            }
            
            return VoiceRecognizer._smoothedVolume;
        },

        // 🔧 新增：计算自适应静音阈值
        calculateAdaptiveThreshold: function(currentVolume) {
            // 在初始阶段收集背景噪音样本
            if (VoiceRecognizer.noiseCalibrationSamples < VoiceRecognizer.maxNoiseCalibrationSamples) {
                // 只在静音时收集背景噪音（避免把语音当作噪音）
                if (!VoiceRecognizer.isSendingAudio) {
                    VoiceRecognizer.backgroundNoiseLevel = 
                        (VoiceRecognizer.backgroundNoiseLevel * VoiceRecognizer.noiseCalibrationSamples + currentVolume) / 
                        (VoiceRecognizer.noiseCalibrationSamples + 1);
                    VoiceRecognizer.noiseCalibrationSamples++;
                    
                    if (VoiceRecognizer.noiseCalibrationSamples === VoiceRecognizer.maxNoiseCalibrationSamples) {
                        console.log('🎯 背景噪音校准完成，噪音水平:', VoiceRecognizer.backgroundNoiseLevel.toFixed(4));
                    }
                }
            }
            
            // 计算自适应阈值
            var baseThreshold = VoiceRecognizer.silenceThreshold;
            var adaptiveThreshold = Math.max(
                baseThreshold,
                VoiceRecognizer.backgroundNoiseLevel * VoiceRecognizer.adaptiveThresholdMultiplier
            );
            
            // 确保阈值在合理范围内
            adaptiveThreshold = Math.min(adaptiveThreshold, 0.1); // 最大阈值0.1
            adaptiveThreshold = Math.max(adaptiveThreshold, 0.005); // 最小阈值0.005
            
            return adaptiveThreshold;
        },

        // 判断是否应该发送音频数据
        shouldSendAudio: function(volume) {
            var now = Date.now();
            VoiceRecognizer.lastVolumeLevel = volume;

            // 🔧 修复：确保录音开始时间已初始化
            if (VoiceRecognizer.recordingStartTime === 0) {
                VoiceRecognizer.recordingStartTime = now;
                console.log('🎤 初始化录音开始时间:', new Date(now).toLocaleTimeString());
            }

            // 🔧 优化：使用动态静音阈值，考虑环境噪音
            var adaptiveThreshold = VoiceRecognizer.calculateAdaptiveThreshold(volume);
            var wasSending = VoiceRecognizer.isSendingAudio === true;

            // 检查是否超过阈值（检测到说话）
            if (volume > adaptiveThreshold) {
                // 从静音/未发送 -> 首次开始发送：重置单轮计时，避免跨很久导致被 maxRecordingTime 限制
                if (!wasSending) {
                    VoiceRecognizer.recordingStartTime = now;
                    VoiceRecognizer.lastResultTime = now;
                    VoiceRecognizer.silenceStartTime = 0;
                    VoiceRecognizer.isSendingAudio = true;
                    console.log('🎯 检测到新一轮发声，已重置本轮计时（recordingStartTime）');
                    return true;
                }

                // 已在发送中，检查单轮最大录音时长
                if (now - VoiceRecognizer.recordingStartTime > VoiceRecognizer.maxRecordingTime) {
                    console.log('⏱️ 达到单轮最大录音时间，暂停发送并等待静音结束');
                    VoiceRecognizer.handleSilence();
                    return false;
                }

                // 持续有声：保持发送
                VoiceRecognizer.silenceStartTime = 0;
                VoiceRecognizer.isSendingAudio = true;
                return true;
            }

            // ----- 静音处理逻辑 -----
            if (VoiceRecognizer.silenceStartTime === 0) {
                VoiceRecognizer.silenceStartTime = now;
                console.log('开始检测静音，当前音量:', volume.toFixed(4), '阈值:', adaptiveThreshold.toFixed(4));
            }

            var silenceDuration = now - VoiceRecognizer.silenceStartTime;
            if (silenceDuration > VoiceRecognizer.silenceTimeout) {
                if (VoiceRecognizer.isSendingAudio) {
                    console.log('静音超时，停止发送音频数据，静音时长:', silenceDuration + 'ms');
                    VoiceRecognizer.handleSilence();
                }
                return false;
            }

            // 短静音宽限期内仍允许发送，避免断断续续
            var shortSilenceGracePeriod = 300; // 300ms的短暂静音容忍期
            if (VoiceRecognizer.isSendingAudio && silenceDuration < shortSilenceGracePeriod) {
                return true;
            }

            return false;
        },

        // 处理静音状态
        handleSilence: function() {
            VoiceRecognizer.isSendingAudio = false;
            console.log('用户停止说话，等待服务端识别结果');

            // 通知Unity端状态变化
            SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'waiting_result');
        },

        // 处理音频数据（支持统一服务模式）
        processAudioData: function(inputBuffer) {
            // 第一步：重采样到16kHz（如果需要）
            var resampledBuffer = VoiceRecognizer.resampleTo16k(inputBuffer);

            // 第二步：转换为16位PCM
            var pcmData = VoiceRecognizer.convertToInt16PCM(resampledBuffer);

            // 第三步：发送PCM数据到WebSocket或Unity
            if (VoiceRecognizer.useUnifiedService) {
                // 统一服务模式：直接发送原始音频数据到Unity，由UnifiedVoiceServiceManager处理
                VoiceRecognizer.sendAudioDataToUnity(pcmData.buffer);
            } else {
                // 传统模式：发送到独立的ASR WebSocket
            if (VoiceRecognizer.webSocket && VoiceRecognizer.webSocket.readyState === WebSocket.OPEN) {
                    console.log('发送音频数据到传统ASR服务 - PCM字节:', pcmData.byteLength, '音量:', VoiceRecognizer.lastVolumeLevel.toFixed(4));
                VoiceRecognizer.webSocket.send(pcmData.buffer);
            } else {
                console.error('无法发送音频数据 - WebSocket未连接');
                }
            }
        },

        // 发送音频数据到Unity（统一服务模式）
        sendAudioDataToUnity: function(audioBuffer) {
            try {
                // 将ArrayBuffer转换为Base64字符串
                var audioBytes = new Uint8Array(audioBuffer);
                var audioDataString = '';
                for (var i = 0; i < audioBytes.length; i++) {
                    audioDataString += String.fromCharCode(audioBytes[i]);
                }
                
                var base64Audio = btoa(audioDataString);
                
                // 🔧 修复：音频数据应该发送到VoiceRecognizerModel，不是WebGLWebSocketAdapter
                console.log('📡 发送音频数据到GameObject:', VoiceRecognizer.gameObjectName, '数据长度:', base64Audio.length);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLAudioData', base64Audio);
                
            } catch (error) {
                console.error('❌ 发送音频数据到Unity异常:', error);
            }
        },

        // 🔧 新增：触发统一服务对话的备用机制
        triggerUnifiedServiceConversation: function() {
            try {
                console.log('🚀 触发统一服务对话启动');
                
                // 检查是否已经在对话中，避免重复启动
                //if (VoiceRecognizer.conversationActive) {
                  //  console.log('⚠️ 对话已激活，跳过重复启动');
                    //return;
                //}
                
                // 检查连接状态
                if (!VoiceRecognizer.unifiedConnectionStatus || 
                    !VoiceRecognizer.unifiedWebSocket || 
                    VoiceRecognizer.unifiedWebSocket.readyState !== WebSocket.OPEN) {
                    console.error('❌ WebSocket未连接，无法启动对话');
                    return;
                }
                
                // 直接调用开始对话逻辑
                console.log('📞 JavaScript直接启动对话（备用机制）');
                var success = VoiceRecognizer.startUnifiedServiceConversation('WebGLUser', 'ali');
                
                if (success) {
                    console.log('✅ 备用对话启动机制成功');
                    
                    // 🔧 设置强制录音超时机制（如果5秒内没收到conversation_started，强制开始录音）
                    setTimeout(function() {
                        if (!VoiceRecognizer.conversationActive && VoiceRecognizer.permissionGranted) {
                            console.log('⚠️ 超时未收到conversation_started，强制启动录音（应急机制）');
                            VoiceRecognizer.isRecording = true;
                            VoiceRecognizer.conversationActive = true;
                            console.log('🎤 强制录音已启动 - isRecording:', VoiceRecognizer.isRecording); 
                        }
                    }, 5000);
                } else {
                    console.error('❌ 备用对话启动机制失败');
                    
                    // 🔧 即使对话启动失败，也尝试强制录音（最后的应急机制）
                    if (VoiceRecognizer.permissionGranted) {
                        console.log('🚨 应急机制：直接启动录音，不依赖服务端状态');
                        setTimeout(function() {
                            VoiceRecognizer.isRecording = true;
                            VoiceRecognizer.conversationActive = true;
                            console.log('🎤 应急录音已启动 - isRecording:', VoiceRecognizer.isRecording);
                        }, 2000);
                    }
                }
                
            } catch (error) {
                console.error('❌ 触发统一服务对话异常:', error);
            }
        },

        // 统一服务：开始对话
        startUnifiedServiceConversation: function(username, asrMode) {
            try {
                // 🔧 修复：检查正确的连接状态和使用正确的WebSocket
                if (!VoiceRecognizer.unifiedConnectionStatus || 
                    !VoiceRecognizer.unifiedWebSocket || 
                    VoiceRecognizer.unifiedWebSocket.readyState !== WebSocket.OPEN) {
                    console.error('❌ 统一服务WebSocket未连接，无法开始对话');
                    return false;
                }

                var startMessage = {
                    action: 'start_conversation',
                    username: username || 'WebGLUser',
                    asr_mode: asrMode || 'ali',
                    conversation_id: 'webgl_conv_' + Date.now()
                };

                console.log('🗣️ 发送开始对话请求:', startMessage);
                VoiceRecognizer.unifiedWebSocket.send(JSON.stringify(startMessage));
                
                return true;
            } catch (error) {
                console.error('❌ 开始统一服务对话异常:', error);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceError', '开始对话异常: ' + error.message);
                return false;
            }
        },

        // 统一服务：发送文本消息
        sendUnifiedServiceText: function(text) {
            try {
                // 🔧 修复：检查正确的连接状态和使用正确的WebSocket
                if (!VoiceRecognizer.unifiedConnectionStatus || 
                    !VoiceRecognizer.unifiedWebSocket || 
                    VoiceRecognizer.unifiedWebSocket.readyState !== WebSocket.OPEN) {
                    console.error('❌ 统一服务WebSocket未连接，无法发送文本');
                    return false;
                }

                var textMessage = {
                    action: 'text_input',
                    text: text,
                    timestamp: Date.now()
                };

                console.log('📝 发送文本到统一服务:', text);
                VoiceRecognizer.unifiedWebSocket.send(JSON.stringify(textMessage));
                
                return true;
            } catch (error) {
                console.error('❌ 发送文本到统一服务异常:', error);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceError', '发送文本异常: ' + error.message);
                return false;
            }
        },

        // 统一服务：停止对话
        stopUnifiedServiceConversation: function() {
            try {
                // 🔧 修复：检查正确的连接状态和使用正确的WebSocket
                if (!VoiceRecognizer.unifiedConnectionStatus || 
                    !VoiceRecognizer.unifiedWebSocket || 
                    VoiceRecognizer.unifiedWebSocket.readyState !== WebSocket.OPEN) {
                    console.warn('⚠️ 统一服务WebSocket未连接，无法发送停止对话请求');
                    return false;
                }

                var stopMessage = {
                    action: 'stop_conversation'
                };

                console.log('🛑 发送停止对话请求');
                VoiceRecognizer.unifiedWebSocket.send(JSON.stringify(stopMessage));
                
                VoiceRecognizer.conversationActive = false;
                return true;
            } catch (error) {
                console.error('❌ 停止统一服务对话异常:', error);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceError', '停止对话异常: ' + error.message);
                return false;
            }
        },
        
        // 检查并开始会话
        checkAndStartSession: function() {
            if (VoiceRecognizer.connectionReady && VoiceRecognizer.permissionGranted && !VoiceRecognizer.sessionActive) {
                console.log('条件满足，开始语音识别会话');
                VoiceRecognizer.startSession();
            }
        },

        // 开始语音识别会话
        startSession: function() {
            if (!VoiceRecognizer.webSocket || VoiceRecognizer.webSocket.readyState !== WebSocket.OPEN) {
                console.error('WebSocket未连接，无法开始会话');
                return;
            }

            try {
                var startCommand = JSON.stringify({
                    action: 'start',
                    mode: 'ali'
                });

                console.log('发送start命令:', startCommand);
                VoiceRecognizer.webSocket.send(startCommand);
                VoiceRecognizer.sessionActive = true;
                VoiceRecognizer.isRecording = true;

                // 完全重置并初始化音频发送控制状态
                var now = Date.now();
                VoiceRecognizer.recordingStartTime = now;
                VoiceRecognizer.lastResultTime = now;
                VoiceRecognizer.isSendingAudio = false;  // 开始时不立即发送，等待检测到声音
                VoiceRecognizer.silenceStartTime = 0;

                console.log('音频发送控制状态已完全重置并初始化，录音开始时间:', new Date(now).toLocaleTimeString());

                // 启动音频上下文
                console.log('音频上下文状态:', VoiceRecognizer.audioContext ? VoiceRecognizer.audioContext.state : 'null');
                if (VoiceRecognizer.audioContext && VoiceRecognizer.audioContext.state === 'suspended') {
                    console.log('尝试恢复音频上下文...');
                    VoiceRecognizer.audioContext.resume().then(function() {
                        console.log('音频上下文恢复成功，当前状态:', VoiceRecognizer.audioContext.state);
                    }).catch(function(error) {
                        console.error('音频上下文恢复失败:', error);
                    });
                }

                console.log('会话状态设置完成 - sessionActive:', VoiceRecognizer.sessionActive, 'isRecording:', VoiceRecognizer.isRecording);
                console.log('音频流状态:', VoiceRecognizer.audioStream ? 'active' : 'null');

                // 启动结果超时检测
                VoiceRecognizer.startResultTimeoutCheck();

                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'session_active');
                console.log('语音识别会话已启动，等待检测到用户声音后开始发送音频数据');

            } catch (error) {
                console.error('启动会话失败:', error);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLError', '启动会话失败: ' + error.message);
            }
        },

        // 处理统一服务消息（支持混合数据流）
        handleUnifiedServiceMessage: function(event) {
            try {
                if (typeof event.data === 'string') {
                    // JSON文本消息
                    VoiceRecognizer.handleUnifiedServiceJsonMessage(event.data);
                } else if (event.data instanceof ArrayBuffer) {
                    // 二进制音频数据
                    VoiceRecognizer.handleUnifiedServiceAudioData(event.data);
                } else {
                    console.warn('⚠️ 收到未知类型的消息:', typeof event.data);
                }
            } catch (error) {
                console.error('❌ 处理统一服务消息异常:', error);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceError', '消息处理异常: ' + error.message);
            }
        },

        // 处理统一服务JSON消息
        handleUnifiedServiceJsonMessage: function(data) {
            try {
                var jsonData = JSON.parse(data);
                console.log('📨 收到统一服务JSON消息:', jsonData);

                // 智能识别消息类型：优先使用type字段，如果没有则根据内容特征判断
                var messageType = jsonData.type;
                if (!messageType) {
                    if (jsonData.status) {
                        messageType = 'status';
                    } else if (jsonData.text && jsonData.is_final !== undefined) {
                        // 🔧 对于真实ASR结果，统一使用 'result' 类型
                        messageType = 'result';
                    } else if (jsonData.text && (jsonData.is_first !== undefined || jsonData.is_end !== undefined)) {
                        messageType = 'llm_result';
                    } else if (jsonData.file_name) {
                        messageType = 'audio_start';
                    } else if (jsonData.total_size !== undefined) {
                        messageType = 'audio_end';
                    } else if (jsonData.error) {
                        messageType = 'error';
                    }
                }

                switch (messageType) {
                    case 'result':
                        // 🔧 处理真实语音识别结果（来自ASR引擎）
                        VoiceRecognizer.handleUnifiedASRResult(jsonData);
                        break;
                    case 'llm_result':
                        VoiceRecognizer.handleUnifiedLLMResult(jsonData);
                        break;
                    case 'audio_start':
                        VoiceRecognizer.handleUnifiedAudioStart(jsonData);
                        break;
                    case 'audio_end':
                        VoiceRecognizer.handleUnifiedAudioEnd(jsonData);
                        break;
                    case 'error':
                        VoiceRecognizer.handleUnifiedError(jsonData);
                        break;
                    case 'status':
                        VoiceRecognizer.handleUnifiedStatus(jsonData);
                        break;
                    default:
                        console.warn('⚠️ 未知的统一服务消息类型:', messageType, '原始数据:', jsonData);
                        break;
                }
            } catch (error) {
                console.error('❌ 解析统一服务JSON消息异常:', error);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceError', 'JSON解析异常: ' + error.message);
            }
        },

        // 处理统一服务音频数据
        handleUnifiedServiceAudioData: function(arrayBuffer) {
            try {
                console.log('🎵 收到统一服务音频数据:', arrayBuffer.byteLength, 'bytes');
                
                // 将ArrayBuffer转换为Uint8Array并传递给Unity
                var audioBytes = new Uint8Array(arrayBuffer);
                var audioDataString = '';
                for (var i = 0; i < audioBytes.length; i++) {
                    audioDataString += String.fromCharCode(audioBytes[i]);
                }
                
                // 将音频数据传递给Unity（使用Base64编码）
                var base64Audio = btoa(audioDataString);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLAudioData', base64Audio);
                
            } catch (error) {
                console.error('❌ 处理统一服务音频数据异常:', error);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceError', '音频数据处理异常: ' + error.message);
            }
        },

        // 处理统一服务ASR结果
        handleUnifiedASRResult: function(data) {
            try {
                console.log('🎤 收到ASR结果:', data.text, '最终:', data.is_final);
                
                // 🔧 修复：直接通过统一服务的WebSocket适配器传递消息
                // 构造完整的ASR结果消息
                var asrMessage = JSON.stringify({
                    type: 'asr_result',  // 使用正确的消息类型
                    text: data.text || '',
                    is_final: data.is_final || false,
                    timestamp: Date.now()
                });
                
                // 发送到统一服务的WebSocket适配器
                SendMessage(VoiceRecognizer.unifiedGameObjectName, 'OnUnifiedWebSocketTextMessage', asrMessage);
                
            } catch (error) {
                console.error('❌ 处理ASR结果异常:', error);
            }
        },

        // 处理统一服务LLM结果
        handleUnifiedLLMResult: function(data) {
            try {
                console.log('🤖 收到LLM结果:', data.text, '首次:', data.is_first, '结束:', data.is_end);
                
                var llmData = JSON.stringify({
                    text: data.text || '',
                    is_first: data.is_first || false,
                    is_end: data.is_end || false,
                    timestamp: Date.now()
                });
                
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLLLMResult', llmData);
                
            } catch (error) {
                console.error('❌ 处理LLM结果异常:', error);
            }
        },

        // 处理统一服务音频开始
        handleUnifiedAudioStart: function(data) {
            try {
                console.log('🔊 音频开始:', data.text);
                
                var audioStartData = JSON.stringify({
                    text: data.text || '',
                    file_name: data.file_name || '',
                    timestamp: Date.now()
                });
                
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLAudioStart', audioStartData);
                
            } catch (error) {
                console.error('❌ 处理音频开始异常:', error);
            }
        },

        // 处理统一服务音频结束
        handleUnifiedAudioEnd: function(data) {
            try {
                console.log('✅ 音频结束:', data.text, '总大小:', data.total_size);
                
                var audioEndData = JSON.stringify({
                    text: data.text || '',
                    total_size: data.total_size || 0,
                    timestamp: Date.now()
                });
                
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLAudioEnd', audioEndData);
                
            } catch (error) {
                console.error('❌ 处理音频结束异常:', error);
            }
        },

        // 处理统一服务错误
        handleUnifiedError: function(data) {
            try {
                console.error('❌ 统一服务错误:', data.message);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceError', data.message || '未知错误');
            } catch (error) {
                console.error('❌ 处理错误消息异常:', error);
            }
        },

        // 处理统一服务状态
        handleUnifiedStatus: function(data) {
            try {
                console.log('📊 统一服务状态:', data.status, data.message || '');
                
                if (data.status === 'ready') {
                    console.log('📊 收到ready状态，启动对话模式');
                    
                    // 将ready视为对话已激活
                    VoiceRecognizer.conversationActive = true;
                    
                    // 立即开始录音（如果权限已获取）
                    if (VoiceRecognizer.permissionGranted) {
                        console.log('🎤 ready状态触发录音启动');
                        VoiceRecognizer.isRecording = true;
                        console.log('📊 录音状态已激活 - isRecording:', VoiceRecognizer.isRecording);
                    } else {
                        console.log('⚠️ 麦克风权限未获取，等待权限后启动录音');
                    }
                    
                    // 通知Unity层状态变化（适配为conversation_started）
                    var adaptedStatusData = JSON.stringify({
                        status: 'conversation_started',
                        original_status: 'ready',
                        message: data.message || 'ASR ready, conversation activated',
                        timestamp: Date.now()
                    });
                    
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceStatus', adaptedStatusData);
                    return; // 避免重复处理
                } else if (data.status === 'conversation_started') {
                    VoiceRecognizer.conversationActive = true;
                    console.log('✅ 统一服务对话已激活');
                    
                    // 🔧 对话激活后自动开始录音
                    if (VoiceRecognizer.permissionGranted) {
                        console.log('🎤 对话激活，开始录音');
                        VoiceRecognizer.isRecording = true;
                        console.log('📊 录音状态设置完成 - isRecording:', VoiceRecognizer.isRecording);
                    } else {
                        console.log('⚠️ 麦克风权限未获取，无法开始录音');
                        console.log('📊 当前权限状态 - permissionGranted:', VoiceRecognizer.permissionGranted);
                    }
                } else if (data.status === 'conversation_ended') {
                    VoiceRecognizer.conversationActive = false;
                    VoiceRecognizer.isRecording = false;
                    console.log('🔚 统一服务对话已结束，停止录音');
                }
                
                var statusData = JSON.stringify({
                    status: data.status || '',
                    message: data.message || '',
                    timestamp: Date.now()
                });
                
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLUnifiedServiceStatus', statusData);
                
            } catch (error) {
                console.error('❌ 处理状态消息异常:', error);
            }
        },

        // 处理传统服务器消息（兼容性保持）
        handleServerMessage: function(data) {
            try {
                // 尝试解析JSON格式
                var jsonData = JSON.parse(data);

                if (jsonData.type === 'result') {
                    console.log('收到识别结果:', jsonData.text, '是否最终:', jsonData.is_final);

                    // 更新最后识别结果时间
                    VoiceRecognizer.lastResultTime = Date.now();

                    // 如果是最终结果，结束当前识别周期
                    if (jsonData.is_final) {
                        console.log('收到最终识别结果，结束当前识别周期');
                        VoiceRecognizer.endRecognitionCycle();
                    }

                    // 构造结构化数据传递给Unity
                    var resultData = JSON.stringify({
                        text: jsonData.text,
                        is_final: jsonData.is_final,
                        timestamp: VoiceRecognizer.lastResultTime
                    });

                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLRecognitionResult', resultData);

                } else if (jsonData.status) {
                    console.log('收到状态消息:', jsonData.status, jsonData.message || '');
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', jsonData.status);
                }

            } catch (error) {
                // 非JSON格式，按原始文本处理（向后兼容）
                console.log('收到原始文本:', data);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLRecognitionResult', data);
            }
        },

        // 启动结果超时检测
        startResultTimeoutCheck: function() {
            if (VoiceRecognizer.resultTimeoutInterval) {
                clearInterval(VoiceRecognizer.resultTimeoutInterval);
            }

            VoiceRecognizer.resultTimeoutInterval = setInterval(function() {
                if (VoiceRecognizer.isRecording && !VoiceRecognizer.isSendingAudio) {
                    var now = Date.now();
                    var timeSinceLastResult = now - VoiceRecognizer.lastResultTime;

                    if (timeSinceLastResult > VoiceRecognizer.resultTimeout) {
                        console.log('识别结果超时，距离最后结果:', timeSinceLastResult + 'ms');
                        console.log('可能需要重新开始识别或结束当前会话');

                        // 通知Unity端超时状态
                        SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'result_timeout');

                        // 可以选择重置状态，允许重新开始发送音频
                        VoiceRecognizer.resetAudioSendingState();
                    }
                }
            }, 1000); // 每秒检查一次
        },

        // 重置音频发送状态
        resetAudioSendingState: function() {
            console.log('重置音频发送状态，允许重新检测声音');
            VoiceRecognizer.isSendingAudio = false;
            VoiceRecognizer.silenceStartTime = 0;
            VoiceRecognizer.lastResultTime = Date.now();
            // 注意：这里不重置recordingStartTime，因为这只是中间状态重置
        },

        // 结束识别周期
        endRecognitionCycle: function() {
            console.log('结束当前识别周期');

            // 停止音频发送
            VoiceRecognizer.isSendingAudio = false;
            VoiceRecognizer.isRecording = false;

            // 重置录音相关时间戳
            VoiceRecognizer.recordingStartTime = 0;
            VoiceRecognizer.silenceStartTime = 0;
            VoiceRecognizer.lastResultTime = 0;

            // 重置识别文本状态
            VoiceRecognizer.lastRecognitionText = "";

            // 停止超时检测
            if (VoiceRecognizer.resultTimeoutInterval) {
                clearInterval(VoiceRecognizer.resultTimeoutInterval);
                VoiceRecognizer.resultTimeoutInterval = null;
            }

            // 通知Unity端识别周期结束
            SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'recognition_complete');

            console.log('识别周期已结束，录音时间戳已重置，等待下一轮开始');
        },

        // 设置录音状态（优化连接状态检查逻辑）
        setRecording: function(isRecording) {
            console.log('设置录音状态:', isRecording);

            if (isRecording) {
                // 🔧 优化：统一连接状态检查逻辑
                if (VoiceRecognizer.useUnifiedService) {
                    // 统一服务模式：检查统一服务WebSocket连接
                    if (!VoiceRecognizer.unifiedWebSocket || 
                        VoiceRecognizer.unifiedWebSocket.readyState !== WebSocket.OPEN ||
                        !VoiceRecognizer.unifiedConnectionStatus) {
                        console.error('❌ 统一服务WebSocket未连接，无法开始录音');
                        console.log('🔍 连接状态调试:', {
                            hasUnifiedSocket: !!VoiceRecognizer.unifiedWebSocket,
                            socketState: VoiceRecognizer.unifiedWebSocket ? VoiceRecognizer.unifiedWebSocket.readyState : 'null',
                            expectedState: WebSocket.OPEN,
                            connectionStatus: VoiceRecognizer.unifiedConnectionStatus,
                            useUnifiedService: VoiceRecognizer.useUnifiedService
                        });
                        return;
                    }
                    
                    // 统一服务模式：检查对话状态
                    if (!VoiceRecognizer.conversationActive) {
                        console.log('🗣️ 统一服务模式，但对话未激活，启动对话');
                        if (VoiceRecognizer.permissionGranted) {
                            // 权限已获取，启动对话
                            console.log('权限已获取，启动统一服务对话');
                            var success = VoiceRecognizer.startUnifiedServiceConversation('WebGLUser', 'ali');
                            if (!success) {
                                console.error('❌ 启动统一服务对话失败');
                                return;
                            }
                        } else {
                            // 权限未获取，请求权限
                            console.log('权限未获取，请求麦克风权限');
                            VoiceRecognizer.requestMicrophonePermission();
                            return;
                        }
                    }
                    
                    // 设置录音状态
                    VoiceRecognizer.isRecording = true;
                    
                    // 🔧 修复：初始化录音时间戳
                    VoiceRecognizer.recordingStartTime = Date.now();
                    VoiceRecognizer.lastResultTime = VoiceRecognizer.recordingStartTime;
                    VoiceRecognizer.isSendingAudio = false;
                    VoiceRecognizer.silenceStartTime = 0;
                    
                    console.log('✅ 统一服务录音状态已设置:', VoiceRecognizer.isRecording, '时间:', new Date().toLocaleTimeString());
                    
                } else {
                    // 传统模式：检查传统WebSocket连接
                    if (!VoiceRecognizer.webSocket || 
                        VoiceRecognizer.webSocket.readyState !== WebSocket.OPEN ||
                        !VoiceRecognizer.connectionReady) {
                        console.error('❌ 传统WebSocket未连接，无法开始录音');
                        console.log('🔍 连接状态调试:', {
                            hasWebSocket: !!VoiceRecognizer.webSocket,
                            socketState: VoiceRecognizer.webSocket ? VoiceRecognizer.webSocket.readyState : 'null',
                            expectedState: WebSocket.OPEN,
                            connectionReady: VoiceRecognizer.connectionReady,
                            useUnifiedService: VoiceRecognizer.useUnifiedService
                        });
                        return;
                    }
                    
                    if (!VoiceRecognizer.sessionActive) {
                        if (VoiceRecognizer.permissionGranted) {
                            // 权限已获取但会话未激活，重新启动会话
                            console.log('权限已获取，重新启动语音识别会话');
                            VoiceRecognizer.startSession();
                        } else {
                            // 权限未获取，请求权限
                            console.log('权限未获取，请求麦克风权限');
                            VoiceRecognizer.requestMicrophonePermission();
                        }
                    } else {
                        // 会话已激活，确保录音状态正确
                        console.log('会话已激活，确保录音状态正确');
                        VoiceRecognizer.isRecording = true;
                    }
                }
            } else {
                // 停止录音
                console.log('停止录音');
                VoiceRecognizer.isRecording = false;
                
                // 🔧 修复：清理录音状态
                VoiceRecognizer.isSendingAudio = false;
                VoiceRecognizer.silenceStartTime = 0;
                VoiceRecognizer.recordingStartTime = 0;
                
                // 清理音量平滑状态
                VoiceRecognizer._smoothedVolume = 0;
                
                console.log('✅ 录音状态已重置');
            }
        },
        
        // 🔧 新增：获取音频检测状态调试信息
        getAudioDetectionStatus: function() {
            var now = Date.now();
            var currentAdaptiveThreshold = VoiceRecognizer.calculateAdaptiveThreshold(VoiceRecognizer.lastVolumeLevel || 0);
            
            return {
                isRecording: VoiceRecognizer.isRecording,
                isSendingAudio: VoiceRecognizer.isSendingAudio,
                lastVolumeLevel: VoiceRecognizer.lastVolumeLevel,
                silenceThreshold: VoiceRecognizer.silenceThreshold,
                adaptiveThreshold: currentAdaptiveThreshold,
                backgroundNoiseLevel: VoiceRecognizer.backgroundNoiseLevel,
                noiseCalibrationProgress: VoiceRecognizer.noiseCalibrationSamples + '/' + VoiceRecognizer.maxNoiseCalibrationSamples,
                silenceStartTime: VoiceRecognizer.silenceStartTime,
                silenceDuration: VoiceRecognizer.silenceStartTime > 0 ? now - VoiceRecognizer.silenceStartTime : 0,
                recordingStartTime: VoiceRecognizer.recordingStartTime,
                recordingDuration: VoiceRecognizer.recordingStartTime > 0 ? now - VoiceRecognizer.recordingStartTime : 0,
                smoothedVolume: VoiceRecognizer._smoothedVolume || 0,
                permissionGranted: VoiceRecognizer.permissionGranted,
                connectionReady: VoiceRecognizer.unifiedConnectionStatus || VoiceRecognizer.connectionReady,
                conversationActive: VoiceRecognizer.conversationActive
            };
        },
        
        // 🔧 新增：重置所有音频检测状态
        resetAllAudioStates: function() {
            console.log('🔄 重置所有音频检测状态');
            VoiceRecognizer.isRecording = false;
            VoiceRecognizer.isSendingAudio = false;
            VoiceRecognizer.silenceStartTime = 0;
            VoiceRecognizer.recordingStartTime = 0;
            VoiceRecognizer.lastResultTime = 0;
            VoiceRecognizer.lastVolumeLevel = 0;
            VoiceRecognizer._smoothedVolume = 0;
            VoiceRecognizer._audioProcessCounter = 0;
            
            // 🔧 重置自适应阈值相关状态
            VoiceRecognizer.backgroundNoiseLevel = 0;
            VoiceRecognizer.noiseCalibrationSamples = 0;
            
            console.log('✅ 音频检测状态已完全重置');
        },
        
        // 统一服务：初始化WebSocket连接
        initUnifiedWebSocket: function(url, gameObjectName) {
            try {
                console.log('🔗 初始化统一服务WebSocket:', url, '游戏对象:', gameObjectName);
                
                // 🔧 记录连接信息，用于重连
                VoiceRecognizer.lastConnectionUrl = url;
                VoiceRecognizer.unifiedGameObjectName = gameObjectName;
                VoiceRecognizer.gameObjectName = gameObjectName;
                
                console.log('🎯 GameObject名称已同步:');
                console.log('  - unifiedGameObjectName:', VoiceRecognizer.unifiedGameObjectName);
                console.log('  - gameObjectName:', VoiceRecognizer.gameObjectName);
                console.log('🔧 准备创建统一服务WebSocket连接...');
                
                // 🔧 清理之前的连接和定时器
                VoiceRecognizer.cleanupConnection();
                
                VoiceRecognizer.unifiedWebSocket = new WebSocket(url);
                VoiceRecognizer.unifiedWebSocket.binaryType = 'arraybuffer';

                VoiceRecognizer.unifiedWebSocket.onopen = function(event) {
                    console.log('✅ 统一服务WebSocket连接成功（onopen事件）');
                    VoiceRecognizer.unifiedConnectionStatus = true;
                    
                    // 🔧 重置连接状态
                    VoiceRecognizer.connectionAttempts = 0;
                    VoiceRecognizer.isReconnecting = false;
                    VoiceRecognizer.lastHeartbeatTime = Date.now();
                    
                    // 🔧 启动心跳机制
                    VoiceRecognizer.startHeartbeat();
                    
                    // 🔧 立即发送连接成功回调到Unity
                    SendMessage(VoiceRecognizer.unifiedGameObjectName, 'OnUnifiedWebSocketConnected', '');
                    console.log('📡 已发送Unity连接成功回调: OnUnifiedWebSocketConnected');
                    
                    // 🔧 统一服务连接成功后自动请求麦克风权限
                    console.log('🎤 统一服务WebSocket连接成功，自动请求麦克风权限');
                    VoiceRecognizer.requestMicrophonePermission();
                    
                    // 🔧 延迟触发开始对话（确保Unity有时间处理连接回调）
                    setTimeout(function() {
                        VoiceRecognizer.triggerUnifiedServiceConversation();
                    }, 500); // 500ms延迟确保Unity处理完连接回调
                };

                VoiceRecognizer.unifiedWebSocket.onmessage = function(event) {
                    // 🔧 修复：确保连接状态正确设置（防止onopen未及时触发）
                    if (!VoiceRecognizer.unifiedConnectionStatus) {
                        console.log('📡 首次收到消息，确保连接状态设置（onopen事件失效）');
                        VoiceRecognizer.unifiedConnectionStatus = true;
                        
                        // 立即发送连接成功回调到Unity
                        SendMessage(VoiceRecognizer.unifiedGameObjectName, 'OnUnifiedWebSocketConnected', '');
                        console.log('📡 已发送Unity连接成功回调: OnUnifiedWebSocketConnected（备用触发）');
                        
                        // 延迟触发开始对话（备用机制）
                        setTimeout(function() {
                            console.log('🔧 备用机制：延迟触发开始对话');
                            VoiceRecognizer.triggerUnifiedServiceConversation();
                        }, 1000); // 1秒延迟，给Unity更多时间处理
                        
                        console.log('✅ 统一服务WebSocket连接成功（通过消息触发）');
                    }
                    
                    // 🔧 修复：使用正确的消息处理机制
                    VoiceRecognizer.handleUnifiedServiceMessage(event);
                    
                    // 同时保持Unity侧的消息通知（用于WebGLWebSocketAdapter）
                    if (typeof event.data === 'string') {
                        SendMessage(VoiceRecognizer.unifiedGameObjectName, 'OnUnifiedWebSocketTextMessage', event.data);
                    } else if (event.data instanceof ArrayBuffer) {
                        var audioBytes = new Uint8Array(event.data);
                        var audioDataString = '';
                        for (var i = 0; i < audioBytes.length; i++) {
                            audioDataString += String.fromCharCode(audioBytes[i]);
                        }
                        var base64Audio = btoa(audioDataString);
                        SendMessage(VoiceRecognizer.unifiedGameObjectName, 'OnUnifiedWebSocketBinaryMessage', base64Audio);
                    }
                };

                VoiceRecognizer.unifiedWebSocket.onclose = function(event) {
                    console.log('🔌 统一服务WebSocket连接关闭, Code:', event.code, 'Reason:', event.reason);
                    VoiceRecognizer.unifiedConnectionStatus = false;
                    
                    // 🔧 停止心跳
                    VoiceRecognizer.stopHeartbeat();
                    
                    // 🔧 如果不是手动关闭，尝试重连
                    if (event.code !== 1000 && !VoiceRecognizer.isReconnecting) {
                        console.log('🔄 连接意外关闭，启动重连机制');
                        VoiceRecognizer.scheduleReconnect();
                    } else {
                        SendMessage(VoiceRecognizer.unifiedGameObjectName, 'OnUnifiedWebSocketDisconnected', '');
                    }
                };

                VoiceRecognizer.unifiedWebSocket.onerror = function(error) {
                    console.error('❌ 统一服务WebSocket错误:', error);
                    VoiceRecognizer.unifiedConnectionStatus = false;
                    
                    // 🔧 停止心跳
                    VoiceRecognizer.stopHeartbeat();
                    
                    // 🔧 启动重连机制
                    if (!VoiceRecognizer.isReconnecting) {
                        console.log('🔄 连接错误，启动重连机制');
                        VoiceRecognizer.scheduleReconnect();
                    }
                    
                    SendMessage(VoiceRecognizer.unifiedGameObjectName, 'OnUnifiedWebSocketError', 'WebSocket连接错误');
                };

            } catch (error) {
                console.error('❌ 统一服务WebSocket初始化失败:', error);
                SendMessage(VoiceRecognizer.unifiedGameObjectName, 'OnUnifiedWebSocketError', '初始化失败: ' + error.message);
            }
        },

        // 统一服务：断开WebSocket连接
        disconnectUnifiedWebSocket: function() {
            try {
                console.log('🔌 手动断开统一服务WebSocket连接');
                
                // 🔧 设置为手动断开，阻止自动重连
                VoiceRecognizer.isReconnecting = false;
                
                // 🔧 使用完整的清理机制
                VoiceRecognizer.cleanupConnection();
                
                VoiceRecognizer.unifiedConnectionStatus = false;
                
                console.log('✅ 统一服务连接已完全断开');
            } catch (error) {
                console.error('❌ 断开统一服务WebSocket异常:', error);
            }
        },

        // 统一服务：发送文本消息
        sendUnifiedTextMessage: function(message) {
            try {
                if (VoiceRecognizer.unifiedWebSocket && VoiceRecognizer.unifiedWebSocket.readyState === WebSocket.OPEN) {
                    console.log('📝 统一服务发送文本消息:', message);
                    VoiceRecognizer.unifiedWebSocket.send(message);
                    console.log('✅ 文本消息已成功发送到unifiedWebSocket');
                } else {
                    console.error('❌ 统一服务WebSocket未连接，无法发送文本');
                    console.error('🔍 调试信息:', {
                        unifiedWebSocket: !!VoiceRecognizer.unifiedWebSocket,
                        readyState: VoiceRecognizer.unifiedWebSocket ? VoiceRecognizer.unifiedWebSocket.readyState : 'null',
                        expectedState: WebSocket.OPEN
                    });
                }
            } catch (error) {
                console.error('❌ 统一服务发送文本消息异常:', error);
            }
        },

        // 统一服务：发送二进制消息
        sendUnifiedBinaryMessage: function(base64Data) {
            try {
                if (VoiceRecognizer.unifiedWebSocket && VoiceRecognizer.unifiedWebSocket.readyState === WebSocket.OPEN) {
                    // 将Base64字符串转换为ArrayBuffer
                    var binaryString = atob(base64Data);
                    var bytes = new Uint8Array(binaryString.length);
                    for (var i = 0; i < binaryString.length; i++) {
                        bytes[i] = binaryString.charCodeAt(i);
                    }
                    
                    console.log('📡 统一服务发送二进制数据:', bytes.length, 'bytes');
                    VoiceRecognizer.unifiedWebSocket.send(bytes.buffer);
                } else {
                    console.error('❌ 统一服务WebSocket未连接，无法发送二进制数据');
                }
            } catch (error) {
                console.error('❌ 统一服务发送二进制消息异常:', error);
            }
        },

        // 统一服务：检查连接状态
        isUnifiedWebSocketConnected: function() {
            return VoiceRecognizer.unifiedConnectionStatus && 
                   VoiceRecognizer.unifiedWebSocket && 
                   VoiceRecognizer.unifiedWebSocket.readyState === WebSocket.OPEN;
        },
        
        // 🔧 连接恢复机制：清理连接和定时器
        cleanupConnection: function() {
            try {
                // 清理重连定时器
                if (VoiceRecognizer.reconnectTimer) {
                    clearTimeout(VoiceRecognizer.reconnectTimer);
                    VoiceRecognizer.reconnectTimer = null;
                }
                
                // 停止心跳
                VoiceRecognizer.stopHeartbeat();
                
                // 清理现有连接
                if (VoiceRecognizer.unifiedWebSocket) {
                    VoiceRecognizer.unifiedWebSocket.onopen = null;
                    VoiceRecognizer.unifiedWebSocket.onmessage = null;
                    VoiceRecognizer.unifiedWebSocket.onclose = null;
                    VoiceRecognizer.unifiedWebSocket.onerror = null;
                    
                    if (VoiceRecognizer.unifiedWebSocket.readyState === WebSocket.OPEN) {
                        VoiceRecognizer.unifiedWebSocket.close();
                    }
                    VoiceRecognizer.unifiedWebSocket = null;
                }
                
                console.log('🧹 连接资源清理完成');
            } catch (error) {
                console.error('❌ 清理连接资源异常:', error);
            }
        },
        
        // 🔧 连接恢复机制：计划重连
        scheduleReconnect: function() {
            if (VoiceRecognizer.isReconnecting) {
                console.log('⚠️ 重连已在进行中，跳过重复请求');
                return;
            }
            
            VoiceRecognizer.connectionAttempts++;
            console.log(`🔄 重连尝试 ${VoiceRecognizer.connectionAttempts}/${VoiceRecognizer.maxReconnectAttempts}`);
            
            if (VoiceRecognizer.connectionAttempts > VoiceRecognizer.maxReconnectAttempts) {
                console.error('❌ 重连次数已达上限，停止重连');
                VoiceRecognizer.isReconnecting = false;
                SendMessage(VoiceRecognizer.unifiedGameObjectName, 'OnUnifiedWebSocketError', '连接失败：重连次数已达上限');
                return;
            }
            
            VoiceRecognizer.isReconnecting = true;
            
            // 计算重连延迟（指数退避策略）
            var delay = Math.min(
                VoiceRecognizer.reconnectDelay * Math.pow(2, VoiceRecognizer.connectionAttempts - 1),
                VoiceRecognizer.maxReconnectDelay
            );
            
            console.log(`⏱️ ${delay}ms 后尝试重连...`);
            
            VoiceRecognizer.reconnectTimer = setTimeout(function() {
                VoiceRecognizer.attemptReconnect();
            }, delay);
        },
        
        // 🔧 连接恢复机制：执行重连
        attemptReconnect: function() {
            try {
                console.log('🔄 正在尝试重连...');
                
                if (!VoiceRecognizer.lastConnectionUrl) {
                    console.error('❌ 没有保存的连接URL，无法重连');
                    VoiceRecognizer.isReconnecting = false;
                    return;
                }
                
                // 使用保存的参数重新初始化连接
                VoiceRecognizer.initUnifiedWebSocket(
                    VoiceRecognizer.lastConnectionUrl, 
                    VoiceRecognizer.unifiedGameObjectName
                );
                
            } catch (error) {
                console.error('❌ 重连异常:', error);
                VoiceRecognizer.isReconnecting = false;
                
                // 继续尝试重连
                VoiceRecognizer.scheduleReconnect();
            }
        },
        
        // 🔧 连接恢复机制：启动心跳
        startHeartbeat: function() {
            VoiceRecognizer.stopHeartbeat(); // 确保没有重复的心跳
            
            VoiceRecognizer.heartbeatInterval = setInterval(function() {
                try {
                    if (VoiceRecognizer.unifiedWebSocket && 
                        VoiceRecognizer.unifiedWebSocket.readyState === WebSocket.OPEN) {
                        
                        // 发送心跳消息
                        var heartbeat = JSON.stringify({
                            type: 'heartbeat',
                            timestamp: Date.now()
                        });
                        
                        VoiceRecognizer.unifiedWebSocket.send(heartbeat);
                        VoiceRecognizer.lastHeartbeatTime = Date.now();
                        console.log('💓 心跳发送成功');
                    } else {
                        console.log('💔 连接已断开，停止心跳');
                        VoiceRecognizer.stopHeartbeat();
                    }
                } catch (error) {
                    console.error('❌ 心跳发送异常:', error);
                    VoiceRecognizer.stopHeartbeat();
                }
            }, VoiceRecognizer.heartbeatDelay);
            
            console.log('💓 心跳机制已启动');
        },
        
        // 🔧 连接恢复机制：停止心跳
        stopHeartbeat: function() {
            if (VoiceRecognizer.heartbeatInterval) {
                clearInterval(VoiceRecognizer.heartbeatInterval);
                VoiceRecognizer.heartbeatInterval = null;
                console.log('💔 心跳机制已停止');
            }
        },
        
        // 🔧 连接恢复机制：手动重连
        manualReconnect: function() {
            console.log('🔄 用户手动触发重连');
            VoiceRecognizer.connectionAttempts = 0; // 重置重连计数
            VoiceRecognizer.isReconnecting = false; // 重置重连状态
            VoiceRecognizer.scheduleReconnect();
        }
    },

    // Unity调用的接口函数
    InitializeWebGLWebSocket: function(url) {
        var urlString = UTF8ToString(url);
        VoiceRecognizer.initWebSocket(urlString);
    },

    InitializeWebGLMicrophone: function() {
        // 不再立即初始化麦克风，改为延迟请求权限
        console.log('WebGL麦克风初始化接口调用（延迟模式）');
    },

    RequestWebGLMicrophonePermission: function() {
        VoiceRecognizer.requestMicrophonePermission();
    },

    SetWebGLRecording: function(isRecording) {
        VoiceRecognizer.setRecording(isRecording);
    },

    // 统一服务接口函数
    SetWebGLUnifiedServiceMode: function(useUnifiedService) {
        VoiceRecognizer.useUnifiedService = useUnifiedService;
        console.log('🔧 WebGL统一服务模式设置为:', VoiceRecognizer.useUnifiedService ? '启用' : '禁用');
    },

    StartWebGLUnifiedServiceConversation: function(username, asrMode) {
        var usernameString = username ? UTF8ToString(username) : 'WebGLUser';
        var asrModeString = asrMode ? UTF8ToString(asrMode) : 'ali';
        return VoiceRecognizer.startUnifiedServiceConversation(usernameString, asrModeString);
    },

    SendWebGLUnifiedServiceText: function(text) {
        var textString = UTF8ToString(text);
        return VoiceRecognizer.sendUnifiedServiceText(textString);
    },

    StopWebGLUnifiedServiceConversation: function() {
        return VoiceRecognizer.stopUnifiedServiceConversation();
    },

    GetWebGLUnifiedServiceState: function() {
        if (!VoiceRecognizer.useUnifiedService) return 0; // Disabled
        if (!VoiceRecognizer.unifiedConnectionStatus) return 1; // Disconnected
        if (!VoiceRecognizer.conversationActive) return 2; // Connected
        return 3; // ConversationActive
    },

    // 兼容性和调试接口
    CopyToClipboard: function(text) {
        console.log('WebGL剪贴板功能暂未实现');
    },

    GetWebGLAudioInfo: function() {
        var info = {
            sampleRate: VoiceRecognizer.sampleRate,
            actualSampleRate: VoiceRecognizer.actualSampleRate,
            needsResampling: VoiceRecognizer.needsResampling,
            permissionGranted: VoiceRecognizer.permissionGranted,
            isRecording: VoiceRecognizer.isRecording,
            useUnifiedService: VoiceRecognizer.useUnifiedService
        };
        return stringToNewUTF8(JSON.stringify(info));
    },

    // 统一服务Unity调用接口
    InitializeUnifiedWebSocket: function(url, gameObjectName) {
        var urlString = UTF8ToString(url);
        var gameObjectNameString = UTF8ToString(gameObjectName);
        VoiceRecognizer.initUnifiedWebSocket(urlString, gameObjectNameString);
    },

    DisconnectUnifiedWebSocket: function() {
        VoiceRecognizer.disconnectUnifiedWebSocket();
    },

    SendUnifiedTextMessage: function(message) {
        var messageString = UTF8ToString(message);
        VoiceRecognizer.sendUnifiedTextMessage(messageString);
    },

    SendUnifiedBinaryMessage: function(base64Data) {
        var base64String = UTF8ToString(base64Data);
        VoiceRecognizer.sendUnifiedBinaryMessage(base64String);
    },

    IsUnifiedWebSocketConnected: function() {
        return VoiceRecognizer.isUnifiedWebSocketConnected();
    },

    // 🔧 新增：音频检测调试接口
    GetWebGLAudioDetectionStatus: function() {
        var status = VoiceRecognizer.getAudioDetectionStatus();
        return stringToNewUTF8(JSON.stringify(status));
    },

    ResetWebGLAudioStates: function() {
        VoiceRecognizer.resetAllAudioStates();
    }
};

// 自动添加依赖和合并到库中
autoAddDeps(VoiceRecognizerPlugin, '$VoiceRecognizer');
mergeInto(LibraryManager.library, VoiceRecognizerPlugin);
