using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using Thry.UdonUtils;

public class UdonMidiPersistence : UdonSharpBehaviour
{
    public string WorldIdentifier;
    [Header("Auto Persistence Fields on Join/Leave")]
    public UdonBehaviour[] BehavioursStrings;
    public string [] FieldsStrings;
    public UdonBehaviour[] BehavioursInts;
    public string [] FieldsInts;
    public UdonBehaviour[] BehavioursFloats;
    public string [] FieldsFloats;

    [Header("Hooks after OnJoin Loading")]
    public UdonBehaviour[] AfterLoadHooksBehaviours;
    public string[] AfterLoadHooksMethods;

    [Header("Hooks before OnLeave Saving")]
    public UdonBehaviour[] BeforeSaveHooksBehaviours;
    public string[] BeforeSaveHooksMethods;

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

    byte[] _currentValueBytes = new byte[4];

    GameObject[] RequestedGameObjectQueue = new GameObject[0];
    string[] RequestedVariableQueue = new string[0];
    GameObject[] RequestedCallbackObjectQueue = new GameObject[0];
    string[] RequestedCallbackMethodQueue = new string[0];

    GameObject _requestGameObject;
    string _requestVariable;
    GameObject _requestCallbackObject;
    string _requestCallbackMethod;

    bool _isInitialized;
    string _lastAutoId;

    public void Start()
    {
        for(int i = 0; i < BehavioursStrings.Length; i++)
        {
            Request(BehavioursStrings[i].gameObject, FieldsStrings[i], null);
            _lastAutoId = BehavioursStrings[i].gameObject.name + ":" + FieldsStrings[i];
        }
        for(int i = 0; i < BehavioursInts.Length; i++)
        {
            Request(BehavioursInts[i].gameObject, FieldsInts[i], null);
            _lastAutoId = BehavioursInts[i].gameObject.name + ":" + FieldsInts[i];
        }
        for(int i = 0; i < BehavioursFloats.Length; i++)
        {
            Request(BehavioursFloats[i].gameObject, FieldsFloats[i], null);
            _lastAutoId = BehavioursFloats[i].gameObject.name + ":" + FieldsFloats[i];
        }
        if(_lastAutoId == null) _isInitialized = true;
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if(player == Networking.LocalPlayer)
        {
            for(int i=0;i<BeforeSaveHooksBehaviours.Length;i++)
                BeforeSaveHooksBehaviours[i].SendCustomEvent(BeforeSaveHooksMethods[i]);

            for(int i = 0; i < BehavioursStrings.Length; i++)
                Save(BehavioursStrings[i].gameObject, FieldsStrings[i]);
            for(int i = 0; i < BehavioursInts.Length; i++)
                Save(BehavioursInts[i].gameObject, FieldsInts[i]);
            for(int i = 0; i < BehavioursFloats.Length; i++)
                Save(BehavioursFloats[i].gameObject, FieldsFloats[i]);
        }
    }

    public void Request(GameObject gameObject, string name, string callback)
    {
        Request(gameObject, name, gameObject.GetComponent<UdonBehaviour>(), callback, WorldIdentifier);
    }

    public void Request(GameObject gameObject, string name, string callback, string dictionaryId)
    {
        Request(gameObject, name, gameObject.GetComponent<UdonBehaviour>(), callback, dictionaryId);
    }

    public void Request(GameObject targetGameObject, string name, UdonBehaviour callbackBehaviour,  string callback, string dictionaryId)
    {
        if(targetGameObject == null) return;
        if(string.IsNullOrEmpty(name)) return;

        EnqueueRequest(targetGameObject, name, callbackBehaviour.gameObject, callback);
        Debug.Log($"[UdonMidiPersistence][Request] {{\"id\":\"{targetGameObject.name}:{name}\", \"dictionaryId\":\"{dictionaryId}\"}}");
    }

    public void Save(GameObject targetGameObject, string name)
    {
        Save(targetGameObject, name, WorldIdentifier);
    }

    public void Save(GameObject targetGameObject, string name, string dictionaryId)
    {
        if(targetGameObject == null) return;
        if(string.IsNullOrEmpty(name)) return;

        UdonBehaviour behaviour = targetGameObject.GetComponent<UdonBehaviour>();

        string value = "";
        System.Type varType = GetVariableType(behaviour, name);
        if(varType == typeof(string))
            value = "\"" + behaviour.GetProgramVariable(name) + "\"";
        else if(varType == typeof(int))
            value = behaviour.GetProgramVariable(name).ToString();
        else if(varType == typeof(float))
            value = ((float)behaviour.GetProgramVariable(name)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        else if(varType == typeof(Vector3))
            value = $"\"{behaviour.GetProgramVariable(name).ToString()}\"";
        else
        {
            Debug.LogError($"[UdonMidiPersistence][ErrorSave] Unsupported type: {varType}");
            return;
        }
        Debug.Log($"[UdonMidiPersistence][Save] {{\"id\":\"{targetGameObject.name}:{name}\", \"value\":{value}, \"type\":\"{varType}\", \"dictionaryId\":\"{dictionaryId}\"}}");
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

    public override void MidiNoteOn(int channel, int midiNumber, int midiValue)
    {
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
                string id = DecodeString(_byteCache, 4, idLength);
                int valueOffset = 4 + idLength;

                // Get target
                string[] parts = id.Split(':');
                if(parts.Length != 2)
                {
                    // invalid value identifier
                    return;
                }
                DequeueRequest();
                while(_requestGameObject != null && (_requestGameObject.name != parts[0] || _requestVariable != parts[1]))
                {
                    if(_requestGameObject.name != parts[0])
                        Debug.LogWarning($"[UdonMidiPersistence] recieved object name {parts[0]} does not match queue head {_requestGameObject.name}. Skipping queue head.");
                    else if(_requestVariable != parts[1])
                        Debug.LogWarning($"[UdonMidiPersistence] recieved variable name {parts[1]} does not match queue head {_requestVariable}. Skipping queue head.");
                    DequeueRequest();
                }
                if(_requestGameObject == null)
                {
                    // invalid gameobject
                    Debug.LogError($"[UdonMidiPersistence] Invalid gameobject: {parts[0]}");
                    return;
                }

                UdonBehaviour behaviour = _requestGameObject.GetComponent<UdonBehaviour>();
                if(behaviour == null)
                {
                    // invalid behaviour
                    Debug.LogError($"[UdonMidiPersistence] Invalid behaviour: {parts[0]}");
                    return;
                }

                if(type != DATA_TYPE_NONE) // is NONE if data is not saved 
                {
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
                    }

                    if(dataType == typeof(string))
                        Debug.Log($"[UdonMidiPersistence][Recieved] {{\"id\":\"{id}\", \"value\":\"{value}\", \"type\":\"{dataType}\"}}");
                    else
                        Debug.Log($"[UdonMidiPersistence][Recieved] {{\"id\":\"{id}\", \"value\":{value}, \"type\":\"{dataType}\"}}");

                    System.Type varType = GetVariableType(behaviour, parts[1]);
                    if(varType != dataType)
                    {
                        // invalid field
                        Debug.LogError($"[UdonMidiPersistence] Recieved datatype {dataType} does not match field type {varType} for {parts[0]}:{parts[1]}");
                        return;
                    }
                    // the variable type will be changed to object cause udon fucking sucks
                    behaviour.SetProgramVariable(parts[1], value);
                }else
                {
                    Debug.Log($"[UdonMidiPersistence][Recieved] {{\"id\":\"{id}\", \"value\":NOT SAVED}}");
                }

                if(_requestCallbackObject != null && _requestCallbackMethod != null)
                {
                    _requestCallbackObject.GetComponent<UdonBehaviour>().SendCustomEvent(_requestCallbackMethod);
                }

                // Check if is init
                if(id == _lastAutoId && !_isInitialized)
                {
                    FinishedInitilization();
                }
            }
        }
    }

    // Need to cache the variable types, because after first setting they will return 'object'
    object[][] _variableTypeDictionary = new object[0][];
    System.Type GetVariableType(UdonBehaviour behaviour, string variableName)
    {
        string index = behaviour.gameObject.name + ":" + variableName;
        foreach(object[] entry in _variableTypeDictionary)
        {
            if(entry[0].Equals(index))
                return (System.Type)entry[1];
        }
        System.Type type = behaviour.GetProgramVariableType(variableName);
        object[][] temp = _variableTypeDictionary;
        _variableTypeDictionary = new object[temp.Length + 1][];
        System.Array.Copy(temp, _variableTypeDictionary, temp.Length);
        _variableTypeDictionary[temp.Length] = new object[]{index, type};
        return type;
    }


    bool DequeueRequest()
    {
        if(RequestedGameObjectQueue.Length == 0)
        {
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
        return true;
    }

    void EnqueueRequest(GameObject go, string variableName, GameObject callbackOb, string callbackMethod)
    {
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
    }

    
}
