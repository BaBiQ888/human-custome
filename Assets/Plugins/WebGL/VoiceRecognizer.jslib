var VoiceRecognizerPlugin = {
    $VoiceRecognizer: {
        webSocket: null,
        mediaRecorder: null,
        audioContext: null,
        audioStream: null,
        isRecording: false,
        gameObjectName: "GameApp",
        websocketUrl: null,

        // 会话状态管理
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

        // 音频处理相关
        sampleRate: 16000,
        bufferSize: 4096,
        audioBuffer: [],

        // 定时器
        resultTimeoutInterval: null,

        // 重采样相关
        needsResampling: false,
        actualSampleRate: 0,
        
        // 初始化WebSocket连接
        initWebSocket: function(url) {
            try {
                VoiceRecognizer.websocketUrl = url;
                VoiceRecognizer.webSocket = new WebSocket(url);

                VoiceRecognizer.webSocket.onopen = function(event) {
                    console.log('WebGL WebSocket连接成功');
                    VoiceRecognizer.connectionReady = true;
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'connected');

                    // 连接成功后，检查是否可以开始会话
                    VoiceRecognizer.checkAndStartSession();
                };

                VoiceRecognizer.webSocket.onmessage = function(event) {
                    console.log('WebGL收到消息:', event.data);
                    VoiceRecognizer.handleServerMessage(event.data);
                };

                VoiceRecognizer.webSocket.onclose = function(event) {
                    console.log('WebGL WebSocket连接关闭');
                    VoiceRecognizer.connectionReady = false;
                    VoiceRecognizer.sessionActive = false;
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'disconnected');
                };

                VoiceRecognizer.webSocket.onerror = function(error) {
                    console.error('WebGL WebSocket错误:', error);
                    VoiceRecognizer.connectionReady = false;
                    SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLError', 'WebSocket连接错误');
                };

            } catch (error) {
                console.error('WebSocket初始化失败:', error);
                SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLError', 'WebSocket初始化失败: ' + error.message);
            }
        },
        
        // 请求麦克风权限（延迟调用）
        requestMicrophonePermission: function() {
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
                    if (VoiceRecognizer.isRecording && VoiceRecognizer.webSocket && VoiceRecognizer.webSocket.readyState === WebSocket.OPEN) {
                        var inputBuffer = event.inputBuffer.getChannelData(0);

                        // 计算当前音频缓冲区的音量
                        var volume = VoiceRecognizer.calculateVolume(inputBuffer);

                        // 检查是否应该发送音频数据
                        if (VoiceRecognizer.shouldSendAudio(volume)) {
                            VoiceRecognizer.processAudioData(inputBuffer);
                        } else {
                            // 不发送音频，但继续监听
                            console.log('跳过音频发送 - 静音或超时，音量:', volume.toFixed(4));
                        }
                    }
                };

                source.connect(processor);
                processor.connect(VoiceRecognizer.audioContext.destination);

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
            for (var i = 0; i < inputBuffer.length; i++) {
                sum += inputBuffer[i] * inputBuffer[i];
            }
            return Math.sqrt(sum / inputBuffer.length);
        },

        // 判断是否应该发送音频数据
        shouldSendAudio: function(volume) {
            var now = Date.now();
            VoiceRecognizer.lastVolumeLevel = volume;

            // 检查最大录音时间
            if (VoiceRecognizer.recordingStartTime > 0 &&
                now - VoiceRecognizer.recordingStartTime > VoiceRecognizer.maxRecordingTime) {
                console.log('达到最大录音时间，停止发送音频');
                return false;
            }

            // 检查音量阈值
            if (volume > VoiceRecognizer.silenceThreshold) {
                // 有声音，重置静音计时
                if (VoiceRecognizer.silenceStartTime > 0) {
                    console.log('检测到声音，音量:', volume.toFixed(4));
                }
                VoiceRecognizer.silenceStartTime = 0;
                VoiceRecognizer.isSendingAudio = true;
                return true;
            } else {
                // 静音检测
                if (VoiceRecognizer.silenceStartTime === 0) {
                    VoiceRecognizer.silenceStartTime = now;
                    console.log('开始检测静音，当前音量:', volume.toFixed(4));
                }

                // 检查静音持续时间
                var silenceDuration = now - VoiceRecognizer.silenceStartTime;
                if (silenceDuration > VoiceRecognizer.silenceTimeout) {
                    if (VoiceRecognizer.isSendingAudio) {
                        console.log('静音超时，停止发送音频数据，静音时长:', silenceDuration + 'ms');
                        VoiceRecognizer.handleSilence();
                    }
                    return false;
                }

                // 静音时间未超时，根据当前状态决定是否继续发送
                return VoiceRecognizer.isSendingAudio;
            }
        },

        // 处理静音状态
        handleSilence: function() {
            VoiceRecognizer.isSendingAudio = false;
            console.log('用户停止说话，等待服务端识别结果');

            // 通知Unity端状态变化
            SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'waiting_result');
        },

        // 处理音频数据
        processAudioData: function(inputBuffer) {
            console.log('processAudioData被调用，原始缓冲区长度:', inputBuffer.length);

            // 第一步：重采样到16kHz（如果需要）
            var resampledBuffer = VoiceRecognizer.resampleTo16k(inputBuffer);
            console.log('重采样后缓冲区长度:', resampledBuffer.length);

            // 第二步：转换为16位PCM
            var pcmData = VoiceRecognizer.convertToInt16PCM(resampledBuffer);
            console.log('PCM数据长度:', pcmData.length, '字节长度:', pcmData.byteLength);

            // 第三步：发送PCM数据到WebSocket
            if (VoiceRecognizer.webSocket && VoiceRecognizer.webSocket.readyState === WebSocket.OPEN) {
                console.log('发送音频数据 - 原始:', inputBuffer.length, '重采样:', resampledBuffer.length, 'PCM字节:', pcmData.byteLength, '音量:', VoiceRecognizer.lastVolumeLevel.toFixed(4));
                VoiceRecognizer.webSocket.send(pcmData.buffer);
                console.log('16位PCM音频数据发送完成');
            } else {
                console.error('无法发送音频数据 - WebSocket未连接');
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

                // 初始化音频发送控制状态
                var now = Date.now();
                VoiceRecognizer.recordingStartTime = now;
                VoiceRecognizer.lastResultTime = now;
                VoiceRecognizer.isSendingAudio = false;  // 开始时不立即发送，等待检测到声音
                VoiceRecognizer.silenceStartTime = 0;

                console.log('音频发送控制状态已初始化');

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

        // 处理服务器消息
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
        },

        // 结束识别周期
        endRecognitionCycle: function() {
            console.log('结束当前识别周期');

            // 停止音频发送
            VoiceRecognizer.isSendingAudio = false;
            VoiceRecognizer.isRecording = false;

            // 停止超时检测
            if (VoiceRecognizer.resultTimeoutInterval) {
                clearInterval(VoiceRecognizer.resultTimeoutInterval);
                VoiceRecognizer.resultTimeoutInterval = null;
            }

            // 通知Unity端识别周期结束
            SendMessage(VoiceRecognizer.gameObjectName, 'OnWebGLConnectionStatus', 'recognition_complete');

            console.log('识别周期已结束，等待下一轮开始');
        },

        // 设置录音状态（保持兼容性，但逻辑简化）
        setRecording: function(isRecording) {
            // 在新的架构下，录音状态由会话管理，这里主要用于兼容
            console.log('设置录音状态:', isRecording);

            if (isRecording && !VoiceRecognizer.sessionActive) {
                // 如果还没有会话，尝试请求权限
                if (!VoiceRecognizer.permissionGranted) {
                    VoiceRecognizer.requestMicrophonePermission();
                }
            }
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

    CopyToClipboard: function(text) {
        console.log('WebGL剪贴板功能暂未实现');
    }
};

// 自动添加依赖和合并到库中
autoAddDeps(VoiceRecognizerPlugin, '$VoiceRecognizer');
mergeInto(LibraryManager.library, VoiceRecognizerPlugin);
