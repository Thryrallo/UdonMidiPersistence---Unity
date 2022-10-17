using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using Thry.UdonUtils;

public class UdonMidiPersistence : UdonSharpBehaviour
{
    public string WorldIdentifier;
    [Header("Auto Persistence Fields on Join/Leave")]
    public UdonBehaviour[] AutoSaveBehaviours;
    public string [] AutoSaveFieldNames;

    [Header("Hooks after OnJoin Loading")]
    public UdonBehaviour[] AfterLoadHooksBehaviours;
    public string[] AfterLoadHooksMethods;

    [Header("Hooks before OnLeave Saving")]
    public UdonBehaviour[] BeforeSaveHooksBehaviours;
    public string[] BeforeSaveHooksMethods;

    [Header("Objects to enable if Midi Persistence available")]
    [Header("Following are updated once first load is done")]
    public GameObject[] EnableIfMidiPersistenceAvailable;
    [Header("Objects to enable if Midi Persistence not available")]
    public GameObject[] EnableIfMidiPersistenceNotAvailable;

    // Incoming data format:
    // Each message has 14 bits

    // ValueIdentier max: 256 bytes
    // String max: 65k bytes

    // Flied values are transmitted encoded as follows:
    // 1st message: 
    //      byte1 : type
    //      byte2: value identifier length
    // n messages: valueIdentifier
    // (if string) n+1 messages: string length = n, string
    // else 1 message: field value

    // valueIdentifier:
    // auto fields: gameobject.name : fieldname

    // Outgoing data: Written to Debug Log
    // as json
    // [UdonMidiPersistence][Save] {"name":"valueIdentifier", "value":"value", dictionaryId:"worldIdentifier}

    // Request data:
    // [UdonMidiPersistence][Request] {"name":"valueIdentifier", dictionaryId:"worldIdentifier"}

    const int DATA_TYPE_NONE = 0;
    const int DATA_TYPE_INT = 1;
    const int DATA_TYPE_FLOAT = 2;
    const int DATA_TYPE_STRING = 3;
    const int DATA_TYPE_VECTOR3 = 4;
    const int DATA_TYPE_FLOAT_ARRAY = 5;

    byte[] _currentValueBytes = new byte[4];

    string[] RequestIdQueue = new string[0];
    GameObject[] RequestedGameObjectQueue = new GameObject[0];
    string[] RequestedVariableQueue = new string[0];
    GameObject[] RequestedCallbackObjectQueue = new GameObject[0];
    string[] RequestedCallbackMethodQueue = new string[0];

    string _requestRequestId;
    GameObject _requestGameObject;
    string _requestVariable;
    GameObject _requestCallbackObject;
    string _requestCallbackMethod;

    bool _isInitialized;
    string _lastAutoId;
    bool _blockInput;
    bool _isMidiPersistenceAvailable;
    bool _isFirstMessagee;

    string _counterPrefix;
    int _counter;

    public void Start()
    {
        if(Networking.LocalPlayer == null) return;
        Random.InitState(Networking.LocalPlayer.playerId << 5 + System.DateTime.Now.Minute << 3 +  System.DateTime.Now.Millisecond);
        _counterPrefix = "";
        _counterPrefix += (char)Random.Range((int)'A', (int)'z' + 1);
        _counterPrefix += (char)Random.Range((int)'A', (int)'z' + 1);
        _counterPrefix += "_"; // Random Prefix to avoid collisions when swapping worlds. 
                               // Randomized instead of deterministic to avoid collisions between two clients running the same world, joining the same minute
        // Make sure world identifier has certain format to prevent accidental overwrites
        if(WorldIdentifier.Contains(":") == false ||
            WorldIdentifier.Split(':')[0].Length < 4)
        {
            Debug.LogError("WorldIdentifier must be at least 4 characters long and contain a colon");
            Debug.LogError("e.g. \"Thry:MyWorld\"");
            _blockInput = true;
            return;
        }
        Debug.Log("[UdonMidiPersistence][Start]");
        for(int i = 0; i < AutoSaveBehaviours.Length; i++)
        {
            Request(AutoSaveBehaviours[i].gameObject, AutoSaveFieldNames[i], null);
        }
        if(_counter == 0) _isInitialized = true;
        else _lastAutoId = _counterPrefix+_counter;

        // Will be updasted once first message is received
        foreach(GameObject obj in EnableIfMidiPersistenceNotAvailable)
        {
            obj.SetActive(true);
        }
        foreach(GameObject obj in EnableIfMidiPersistenceAvailable)
        {
            obj.SetActive(false);
        }
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if(_blockInput) return;
        if(player == Networking.LocalPlayer)
        {
            for(int i=0;i<BeforeSaveHooksBehaviours.Length;i++)
                BeforeSaveHooksBehaviours[i].SendCustomEvent(BeforeSaveHooksMethods[i]);

            for(int i = 0; i < AutoSaveBehaviours.Length; i++)
                Save(AutoSaveBehaviours[i].gameObject, AutoSaveFieldNames[i]);
        }
    }

    /// <summary>
    /// Request a value from the app. Uses the world id dictionary.
    /// </summary>
    /// <param name="gameObject">The game object to request the value from</param>
    /// <param name="variableName">The field to load</param>
    /// <param name="callback">The udon event to call once the field has been loaded</param>
    public void Request(GameObject gameObject, string variableName, string callback)
    {
        Request(WorldIdentifier, $"{gameObject.name}:{variableName}", gameObject, variableName, gameObject, callback);
    }

    /// <summary>
    /// Request a value from the app. Uses a custom dictionary.
    /// </summary>
    /// <param name="gameObject">The game object to request the value from</param>
    /// <param name="variableName">The field to load</param>
    /// <param name="callback">The udon event to call once the field has been loaded</param>
    /// <param name="dictionaryId">The dictionary to use</param>
    public void Request(GameObject gameObject, string variableName, string callback, string dictionaryId)
    {
        Request(dictionaryId, $"{gameObject.name}:{variableName}", gameObject, variableName, gameObject, callback);
    }

    /// <summary>
    /// Request a saved value from the app. Use the Request Method, if you are not 100% sure how this method works.
    /// </summary>
    /// <param name="dictionaryId">The dictionary to get the field from</param>
    /// <param name="dictionaryKey">The key to get</param>
    /// <param name="targetGameObject">The gameobject that contains the field</param>
    /// <param name="variableName">The name of the field</param>
    /// <param name="callbackGameObject">The gameobject that contains the callback method</param>
    /// <param name="callbackMethodName">The name of the callback method</param>
    public void Request(string dictionaryId, string dictionaryKey, GameObject targetGameObject, string variableName, GameObject callbackBehaviour,  string callback)
    {
        if(_blockInput) return;
        if(targetGameObject == null) return;
        if(string.IsNullOrEmpty(name)) return;

        string reqId = EnqueueRequest(targetGameObject, variableName, callbackBehaviour, callback);
        Debug.Log($"[UdonMidiPersistence][Request] {{\"reqId\":\"{reqId}\" ,\"id\":\"{dictionaryKey}\", \"dictionaryId\":\"{dictionaryId}\"}}");
    }

    /// <summary>
    /// Save a value to the app. Uses the world id dictionary.
    /// </summary>
    /// <param name="gameObject">The game object/udon behaviour to save the value from</param>
    /// <param name="variableName">The field to save</param>
    public void Save(GameObject targetGameObject, string variableName)
    {
        Save(WorldIdentifier, $"{targetGameObject.name}:{variableName}", targetGameObject, variableName, null);
    }
    
    /// <summary>
    /// Save a value to the app. Use the Save Method, only if you are not 100% sure how this method works.
    /// </summary>
    /// <param name="targetGameObject">The gameobject/udon behaviour that contains the field</param>
    /// <param name="targetFieldName">The name of the field</param>
    /// <param name="dictionaryId">The dictionary to save the field to</param>
    /// <param name="dictionaryKey">The key to save the field to</param>
    public void Save(string dictionaryId, string dictionaryKey, GameObject targetGameObject, string variableName, System.Type variableType)
    {
        // GetProgramVariableType returns null as soon as the varialbe is set anywhere by udon
        // This is why the type should be passed in if doing custom logic where the first action is not a request or load that can cache the type
        if(_blockInput) return;
        if(targetGameObject == null) return;
        if(string.IsNullOrEmpty(variableName)) return;

        UdonBehaviour behaviour = targetGameObject.GetComponent<UdonBehaviour>();
        
        variableType = GetVariableType(behaviour, variableName, variableType);

        string value = "";
        object variableValue = behaviour.GetProgramVariable(variableName);
        float[] floatArray = null; // needs special variable because udon will throw a fit else

        // Lots of special math types are converted to float arrays to cut down on logic
        if(variableType == typeof(float[]))
        {
            floatArray = (float[])variableValue;
        }
        if(variableType == typeof(Color))
        {
            Color c = (Color)variableValue;
            floatArray = new float[] { c.r, c.g, c.b, c.a };
            variableType = typeof(float[]);
        }

        if(variableType == typeof(string))
            value = "\"" + variableValue + "\"";
        else if(variableType == typeof(int))
            value = variableValue.ToString();
        else if(variableType == typeof(float))
            value = ((float)variableValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
        else if(variableType == typeof(Vector3))
            value = $"\"{variableValue.ToString()}\"";
        else if(variableType == typeof(float[]))
        {
            value = "[";
            for(int i = 0; i < floatArray.Length; i++)
            {
                value += floatArray[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                if(i < floatArray.Length - 1)
                    value += ",";
            }
            value += "]";
        } 
        else
        {
            Debug.LogError($"[UdonMidiPersistence][ErrorSave] Unsupported type: {variableType}");
            return;
        }
        Debug.Log($"[UdonMidiPersistence][Save] {{\"id\":\"{dictionaryKey}\", \"value\":{value}, \"type\":\"{variableType}\", \"dictionaryId\":\"{dictionaryId}\"}}");
    }

    public void FinishedInitilization()
    {
        Debug.Log("[UdonMidiPersistence] Finished Initialization. Calling OnLoad Callbacks.");
        _isInitialized = true;
        for(int i = 0;i<AfterLoadHooksBehaviours.Length;i++)
        {
            AfterLoadHooksBehaviours[i].SendCustomEvent(AfterLoadHooksMethods[i]);
        }
    }

    public bool IsInitialized()
    {
        return _isInitialized;
    }

    byte _lastPartialByte;
    byte _lastPartialByteBits;
    byte[] _byteCache = new byte[65536];
    int _byteCacheLength = 0;

    string DecodeString(byte[] bytes, int offset, int length)
    {
        string s = "";
        for(int i = 0; i < length; i++)
        {
            s += (char)bytes[offset + i];
        }
        return s;
    }

    //int midiCount = 0;
    public override void MidiNoteOn(int channel, int midiNumber, int midiValue)
    {
        //Debug.Log("Midi : "+   (++midiCount));
        // Debug.Log("Midi recieved: "+ channel + " " + midiNumber + " " + midiValue);
        int bits = (midiNumber << 7) + (midiValue); // 14 bits
        // add last partical bits to front
        bits = bits + (_lastPartialByte << 14);
        byte bitsLength = (byte)(14 + _lastPartialByteBits);
        // split into bytes
        byte b1 = (byte)(bits >> (bitsLength - 8));
        //Debug.Log(_byteCacheLength + ": "+ b1);
        _byteCache[_byteCacheLength++] = b1;
        if(bitsLength >= 16)
        {
            byte b2 = (byte)((bits >> (bitsLength - 16)) & 0xFF);
            //Debug.Log(_byteCacheLength + ": "+ b2);
            _byteCache[_byteCacheLength++] = b2;
            _lastPartialByteBits = (byte)(bitsLength - 16);
        }else
        {
            _lastPartialByteBits = (byte)(bitsLength - 8);
        }
        _lastPartialByte = (byte)(((bits << (8 - _lastPartialByteBits)) & 0xFF) >> (8 - _lastPartialByteBits));

        if(_byteCacheLength > 3)
        {
            // Check if we have a full message
            int length = _byteCache[0] * 256 + _byteCache[1];
            //Debug.Log("Check byte cache length: "+ _byteCacheLength + " >= " + length);
            if(_byteCacheLength >= length)
            {
                _byteCacheLength = 0;
                _lastPartialByte = 0;
                _lastPartialByteBits = 0;

                // Decode message
                int idLength = _byteCache[2];
                int type = _byteCache[3];
                string reqId = DecodeString(_byteCache, 4, idLength);
                int valueOffset = 4 + idLength;

                // Get target
                if(string.IsNullOrEmpty(reqId))
                {
                    // invalid value identifier
                    Debug.LogError("[UdonMidiPersistence][Error] Invalid value identifier");
                    return;
                }
                DequeueRequest();
                while(_requestGameObject != null && reqId != _requestRequestId)
                {
                    Debug.LogWarning($"[UdonMidiPersistence] request id {reqId} does not match queue head {_requestRequestId}. Skipping queue head.");
                    DequeueRequest();
                }
                if(_requestGameObject == null)
                {
                    // invalid gameobject
                    Debug.LogError($"[UdonMidiPersistence] Invalid gameobject: {reqId}");
                    return;
                }

                UdonBehaviour behaviour = _requestGameObject.GetComponent<UdonBehaviour>();
                if(behaviour == null)
                {
                    // invalid behaviour
                    Debug.LogError($"[UdonMidiPersistence] Invalid behaviour: {reqId}");
                    return;
                }

                if(type != DATA_TYPE_NONE) // is NONE if data is not saved 
                {
                    System.Type varType = GetVariableType(behaviour, _requestVariable, null);
                    // Decode value
                    object value = null;
                    System.Type dataType = null;
                    if(type == DATA_TYPE_INT)
                    {
                        value = BitConverter.ToInt32(new byte[]{_byteCache[valueOffset+3], _byteCache[valueOffset+2], _byteCache[valueOffset+1], _byteCache[valueOffset+0]});
                        dataType = typeof(int);
                    }else if(type == DATA_TYPE_FLOAT)
                    {
                        value = BitConverter.ToSingle(new byte[]{_byteCache[valueOffset+3], _byteCache[valueOffset+2], _byteCache[valueOffset+1], _byteCache[valueOffset+0]});
                        dataType = typeof(float);
                    }else if(type == DATA_TYPE_STRING)
                    {
                        int valueLength = _byteCache[valueOffset+0] * 256 + _byteCache[valueOffset+1];
                        value = DecodeString(_byteCache, valueOffset+2, valueLength);
                        dataType = typeof(string);
                    }else if(type == DATA_TYPE_VECTOR3)
                    {
                        float x = BitConverter.ToSingle(new byte[]{_byteCache[valueOffset+3], _byteCache[valueOffset+2], _byteCache[valueOffset+1], _byteCache[valueOffset+0]});
                        float y = BitConverter.ToSingle(new byte[]{_byteCache[valueOffset+7], _byteCache[valueOffset+6], _byteCache[valueOffset+5], _byteCache[valueOffset+4]});
                        float z = BitConverter.ToSingle(new byte[]{_byteCache[valueOffset+11], _byteCache[valueOffset+10], _byteCache[valueOffset+9], _byteCache[valueOffset+8]});
                        value = new Vector3(x, y, z);
                        dataType = typeof(Vector3);
                    }else if(type == DATA_TYPE_FLOAT_ARRAY)
                    {
                        int arrayLength = _byteCache[valueOffset+0] * 256 + _byteCache[valueOffset+1];
                        float[] array = new float[arrayLength];
                        for(int i = 0; i < arrayLength; i++)
                        {
                            array[i] = BitConverter.ToSingle(new byte[]{_byteCache[valueOffset+5+i*4], _byteCache[valueOffset+4+i*4], _byteCache[valueOffset+3+i*4], _byteCache[valueOffset+2+i*4]});
                        }
                        if(varType == typeof(Color))
                        {
                            value = new Color(array[0], array[1], array[2], array[3]);
                            dataType = typeof(Color);
                        }
                    }

                    Debug.Log($"[UdonMidiPersistence][Recieved] {behaviour.name}:{_requestVariable} => {value}");
                    
                    if(varType != dataType)
                    {
                        // invalid field
                        Debug.LogError($"[UdonMidiPersistence] Recieved datatype {dataType} does not match field type {varType} for {behaviour.name}:{_requestVariable}");
                        return;
                    }
                    // the variable type will be changed to object cause udon fucking sucks
                    behaviour.SetProgramVariable(_requestVariable, value);
                }else
                {
                    Debug.Log($"[UdonMidiPersistence][Recieved] {behaviour.name}:{_requestVariable}) => [NOT SAVED]");
                }

                if(_requestCallbackObject != null && _requestCallbackMethod != null)
                {
                    _requestCallbackObject.GetComponent<UdonBehaviour>().SendCustomEvent(_requestCallbackMethod);
                }

                // Check if is init
                if(reqId == _lastAutoId && !_isInitialized)
                {
                    FinishedInitilization();
                }

                if(_isFirstMessagee)
                {
                    _isMidiPersistenceAvailable = true;
                    _isFirstMessagee = false;
                    foreach(GameObject obj in EnableIfMidiPersistenceAvailable)
                    {
                        obj.SetActive(true);
                    }
                    foreach(GameObject obj in EnableIfMidiPersistenceNotAvailable)
                    {
                        obj.SetActive(false);
                    }
                }
            }
        }
    }

    // Need to cache the variable types, because after first setting they will return 'object'
    object[][] _variableTypeDictionary = new object[0][];
    System.Type GetVariableType(UdonBehaviour behaviour, string variableName, System.Type set)
    {
        string index = behaviour.gameObject.name + ":" + variableName;
        foreach(object[] entry in _variableTypeDictionary)
        {
            if(entry[0].Equals(index))
            {
                if(set != null)
                {
                    entry[1] = set;
                }
                return (System.Type)entry[1];
            }
        }
        if(set == null) set = behaviour.GetProgramVariableType(variableName);
        object[][] temp = _variableTypeDictionary;
        _variableTypeDictionary = new object[temp.Length + 1][];
        System.Array.Copy(temp, _variableTypeDictionary, temp.Length);
        _variableTypeDictionary[temp.Length] = new object[]{index, set};
        return set;
    }


    bool DequeueRequest()
    {
        if(RequestedGameObjectQueue.Length == 0)
        {
            _requestRequestId = null;
            _requestGameObject = null;
            _requestVariable = null;
            _requestCallbackObject = null;
            _requestCallbackMethod = null;
            return false;
        }
        _requestGameObject = RequestedGameObjectQueue[0];
        GameObject[] temp = RequestedGameObjectQueue;
        RequestedGameObjectQueue = new GameObject[temp.Length - 1];
        System.Array.Copy(temp, 1, RequestedGameObjectQueue, 0, temp.Length - 1);
        _requestCallbackObject = RequestedCallbackObjectQueue[0];
        temp = RequestedCallbackObjectQueue;
        RequestedCallbackObjectQueue = new GameObject[temp.Length - 1];
        System.Array.Copy(temp, 1, RequestedCallbackObjectQueue, 0, temp.Length - 1);
        _requestCallbackMethod = RequestedCallbackMethodQueue[0];
        string[] temp2 = RequestedCallbackMethodQueue;
        RequestedCallbackMethodQueue = new string[temp2.Length - 1];
        System.Array.Copy(temp2, 1, RequestedCallbackMethodQueue, 0, temp2.Length - 1);
        _requestVariable = RequestedVariableQueue[0];
        temp2 = RequestedVariableQueue;
        RequestedVariableQueue = new string[temp2.Length - 1];
        System.Array.Copy(temp2, 1, RequestedVariableQueue, 0, temp2.Length - 1);
        _requestRequestId = RequestIdQueue[0];
        temp2 = RequestIdQueue;
        RequestIdQueue = new string[temp2.Length - 1];
        System.Array.Copy(temp2, 1, RequestIdQueue, 0, temp2.Length - 1);
        return true;
    }

    string EnqueueRequest(GameObject go, string variableName, GameObject callbackOb, string callbackMethod)
    {
        string reqId = _counterPrefix+_counter++;
        GameObject[] temp = RequestedGameObjectQueue;
        RequestedGameObjectQueue = new GameObject[temp.Length + 1];
        System.Array.Copy(temp, RequestedGameObjectQueue, temp.Length);
        RequestedGameObjectQueue[temp.Length] = go;
        temp = RequestedCallbackObjectQueue;
        RequestedCallbackObjectQueue = new GameObject[temp.Length + 1];
        System.Array.Copy(temp, RequestedCallbackObjectQueue, temp.Length);
        RequestedCallbackObjectQueue[temp.Length] = callbackOb;
        string[] temp2 = RequestedCallbackMethodQueue;
        RequestedCallbackMethodQueue = new string[temp2.Length + 1];
        System.Array.Copy(temp2, RequestedCallbackMethodQueue, temp2.Length);
        RequestedCallbackMethodQueue[temp2.Length] = callbackMethod;
        temp2 = RequestedVariableQueue;
        RequestedVariableQueue = new string[temp2.Length + 1];
        System.Array.Copy(temp2, RequestedVariableQueue, temp2.Length);
        RequestedVariableQueue[temp2.Length] = variableName;
        temp2 = RequestIdQueue;
        RequestIdQueue = new string[temp2.Length + 1];
        System.Array.Copy(temp2, RequestIdQueue, temp2.Length);
        RequestIdQueue[temp2.Length] = reqId;
        return reqId;
    }

    
}
