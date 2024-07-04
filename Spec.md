# OPC UA PubSub Interface Description for umati

This document defines the Interface of the umati Dashboard based on OPC UA PubSub.
It describes how a OPC UA server address space must be mapped to OPC UA PubSub, so that the information can map to the umati Dashboard templates and displayed.

## Requirements for the Mapping of the address space to PubSub

(Reference: [umati/UA-CloudPublisher#5](https://github.com/umati/UA-CloudPublisher/issues/5))

- Ability to react to dynamic changes in the address space (nodes added/deleted)
  - Changes should be detected by monitoring NodeManagement events.
- Resolve references to nodes (NodeId as values)
- Hierarchy references must be mapped
- Non-hierarchy references should be mapped if necessary
  - Non-hierarchy references are mapped if relevant to the data semantics.
- Provide type information
- Methods should be considered
- Online detection (possibly as separate topics)
- Allow empty objects
- Events/alarms must be mappable
- Coordinate the completeness of the run
- Encoding should be UA JSON
- Dimensioning for approximately 350 subscribers (machines + dashboard users)
- Custom DataTypes must be included
- The address space includes at least OPC UA Machinery and possibly other Companion Specifications based on it.
- The entry point of the mapping is the machine object in the Machines folder of Machinery.

## Status of the Publisher

As definied in OPC UA Part 14 the status should be sended at the following topic:
`opcua/json/status/<PublisherId>` with  `<PublisherId>` is the name of the Client
The value of the Field "IsCyclic" should be `true` and the Status should be published at least every 90 Sec a higher frequency is possible.

## Application Description

The Application can be send optional.

## Connection

As definied in OPC UA Part 14 the connection should be sended at the following topic:
`opcua/json/connection/<PublisherId>` with  `<PublisherId>` is the name of the Client.
The following parameter must be set:

- `WriterGroups.DataSetWriters.QueueName`
- `WriterGroups.DataSetWriters.MetaDataQueueName`

## Mapping

### Gerneral

- For identification of nodes, the name of the DataSet with expanded NodeId must be used.

### Mapping of Objects and Variables

- An object is mapped to a DataSet.
- A variable is mapped as a field to the DataSet. If the variable contains children (other variables), a DataSet with the children is created.

#### Example

![image](https://github.com/umati/UA-CloudPublisher/assets/12342602/91eae1d7-9d6f-4246-abb2-0ec5ea9b59bc)

The image shows a part of an address space. Each object is mapped to a DataSet:

- Production
- ActiveProgram
- State

The properties Name, NumberInList, Id, and Number are fields of the DataSets. The value of CurrentState is mapped as a field of State and has a DataSet with its properties:

- Production
- ActiveProgram
  - Name
  - NumberInList
  - CurrentState
- State
- CurrentState
  - Id
  - Number

### Explanation

A field can also contain properties but no other variables, even though the address space can contain more hierarchies. Properties of objects also cannot be mapped to the properties of a field. Therefore, a uniform mapping is used for objects and variables so that access is always consistent.

## Mapping of Events

Events are also mapped to a DataSet. Cause Event have no BrowsePath, the BrowsePath of the SourceName and the EventName is used. The mapping itself is analogous to the mapping of objects.

## Topic Structure of DataSet and MetaDataSet

The generic topic structure for OPC UA is:

`<Prefix>/<Encoding>/<MqttMessageType>/<PublisherId>/[<WriterGroup>[/<DataSetWriter>]]`

The mapping is based on the structure but deviates from it if necessary. The connection topic (see above) can still be used to read out the exact topic.

| Key               | Description                                                  |
|-------------------|--------------------------------------------------------------|
| `<Prefix>`        | umati/v3                                                     |
| `<Encoding>`      | json                                                         |
| `<MqttMessageType>` | as defined in OPC UA PubSub                                |
| `<PublisherId>`   | Name of the Client. Can be used for UNS Structure            |
| `[<WriterGroup>[/<DataSetWriter>]]` | PathToTheNode                              |

The PathToTheNode is the Path from the 0:Objects node to the Node that is connected to the DataSet.
Generally, only hierarchical references are used. If a node occurs in two places, the message should be sent to both topics.
The use of Organizes references can lead to loops in the path. In this case, only the shortest path should be transmitted.
Each Node is a topic. The Topic name is build from the `name` field of the BrowseName. All character except `[A-Za-z0-9]` need to encode by [URL-Encoding](https://de.wikipedia.org/wiki/URL-Encoding) using an underscore instead of a '%'.
If two node has the same BrowsePath a iterator (".Number") can be send to avoid collisions (e.g, `Parent/Tool.1`, `Parent.3/Tool.2` `Parent3/Tool.3` )

For Events the SourceNode and the Name are used for the PathToTheNode

### Examples

DataSet Topic

```text
umati/v3/json/data/example_publisher_1/machines/ShowcaseMachineTool/Identification
umati/v3/json/data/example_publisher_1/machines/ShowcaseMachineTool/Production/ActiveProgram
umati/v3/json/data/example_publisher_1/machines/ShowcaseMachineTool/Production/ActiveProgram/State
```

DataSet Event Topic

```text
umati/v3/json/data/example_publisher_1/machines/ShowcaseMachineTool/Production/ActiveProgram/State/TransitionEventType
```

MetaDataSet Topic

```text
umati/v3/json/metadata/example_publisher_1/machines/ShowcaseMachineTool/Identification
umati/v3/json/metadata/example_publisher_1/machines/ShowcaseMachineTool/Production/ActiveProgram
umati/v3/json/metadata/example_publisher_1/machines/ShowcaseMachineTool/Production/ActiveProgram/State
```

## How to annotate the DataSet with semantics (e.g. TypeDefinition, References)

Here we see three possible variants of how this semantic information can be transferred. A decision as to which of the variants should be implemented has not yet been made.

### Variante A Encoding as Field

This variant defines new Fields in the DataSet for the meta data if he meta data that cannot be stored in an existing entry.

#### DataSetMeta

The fields of DataSetMeta should be mapped as follows:

- Namespaces = as defined in Part 14
- StructureDataTypes = as defined in Part 14
- Name = BrowsePath with NamespaceUri instead of namespace index
- Description = as defined in Part 14
- Fields = Contains the field description of the variables/properties
  - Name = ExpandedBrowseName (NamespaceUri#Name)
  - Properties = Not used
  - Other properties as defined in Part 14
- DataSetClassId = as defined in Part 14
- ConfigurationVersion = as defined in Part 14

If two node has the same BrowsePath a iterator (".Number") can be send to avoid collisions (e.g, `Parent/Tool.1`, `Parent.3/Tool.2` `Parent3/Tool.3` )

#### Fix Field in the DataSet

The description field needs to contain the following entries. The JSON object can be extended vendor-specifically if necessary.

| Name                 | DataType       | Dimension | ModellingRule | Description                                                     |
|----------------------|----------------|-----------|---------------|-----------------------------------------------------------------|
| umati_Types                | QualifiedName     | Array    | optional      | Name of the type of the ObjectType or VariableType. Each empty in the array represents a supertype of the node. The first entry is the type itself.              |
| umati_TypeNodeID           | ExtendedNodeId | Scalar    |               | NodeId of the type of the ObjectType or VariableType            |
| umati_AdditionalBrowsePath | String         | Scalar    | optional      | If additional BrowsePaths exist, they can be included here      |
| umati_AdditionalReference  | KeyValuePair   | Array     | optional      | Additional references from the object, e.g., GenerateEvent      |
| umati_Description          | String         | Scalar    | optional      | Description as defined for the description field in Part 14     |

#### DataSetMeta Example

```json
{
  "Namespaces": [
    "http://opcfoundation.org/UA/",
    "urn:DEMO-5:UA Sample Server",
    "http://opcfoundation.org/UA/DI/",
    "http://opcfoundation.org/UA/Machinery/",
    "http://opcfoundation.org/UA/IA/",
    "http://opcfoundation.org/UA/MachineTool/",
    "urn:Demo:MachineTool:myMachine/"
  ],
  "StructureDataTypes": "...",
  "Name": "5:Production.5:ActiveProgram.5:State",
  "Description": "my Description",
  "Fields": [{
    "Name": "humati_TypeNodeID",
    "Description": "as in Part 14",
    "FieldFlags": "as in Part 14",
    "BuiltInType": "12",
    "DataType": {"id": 12},
    "ValueRank": -1,
    "ArrayDimensions": "as in Part 14",
    "MaxStringLength": "as in Part 14",
    "DataSetFieldId": "as in Part 14",
    "Properties": "not used"
  },
  ...
  {
    "Name": "http://opcfoundation.org/UA/Machinery/#name",
    "Description": "as in Part 14",
    "FieldFlags": "as in Part 14",
    "BuiltInType": "12",
    "DataType": {"id": 12},
    "ValueRank": -1,
    "ArrayDimensions": "as in Part 14",
    "MaxStringLength": "as in Part 14",
    "DataSetFieldId": "as in Part 14",
    "Properties": "not used"
  },{
    "Name": "http://opcfoundation.org/UA/Machinery/#NumberInList",
    "Description": "as in Part 14",
    "FieldFlags": "as in Part 14",
    "BuiltInType": "5",
    "DataType": {"id": 5},
    "ValueRank": -1,
    "ArrayDimensions": "as in Part 14",
    "MaxStringLength": "as in Part 14",
    "DataSetFieldId": "as in Part 14",
    "Properties": "not used"
  }]
}
```

#### DateSet

The DataSet follows the MetaData and other definitions of the specification. A DataSet for the umati Dashboard needs to contain at least the Payload as RowData and a DataSet message header with the following fields:

- Timestamp
- Status
- Name

#### DateSet Example

```json
{
  "Timestamp": "2021-09-27T18:45:19.555Z",
  "Status": 1073741824,
  "Name": "5:Production.5:ActiveProgram.5:State",
  "Payload": {
	"umati_Types":["5:ProductionProgramStateMachineType","0:FinteStateMachineType",...,"0:BaseObjectType"]
	"umati_TypeNodeID" : "nsu=http://opcfoundation.org/UA/Machinery/;i=58997",
	"umati_AdditionalReference" : [
      {"0:GeneratesEvents":"nsu=http://opcfoundation.org/UA/Machinery/;i=3444"}
    ]
    "Name": "Basic Program",
    "NumberInList": 0
  }
}
```


### Variante B Encoding as JSON in the Description of the DataSetMetaData

This variant takes over the separation between DataSet and DataSetMeta and maps the meta data in the DataSetMeta. In order to remain standard-compliant, the meta data that cannot be stored in an existing entry is encoded in a JSON object in the description.

#### DataSetMeta

The fields of DataSetMeta should be mapped as follows:

- Namespaces = as defined in Part 14
- StructureDataTypes = as defined in Part 14
- Name = BrowsePath with NamespaceUri instead of namespace index
- Description = JSON object containing additional semantics (e.g., references)
- Fields = Contains the field description of the variables/properties
  - Name = ExpandedBrowseName (NamespaceUri#Name)
  - Properties = Not used
  - Other properties as defined in Part 14
- DataSetClassId = as defined in Part 14
- ConfigurationVersion = as defined in Part 14

If two node has the same BrowsePath a iterator (".Number") can be send to avoid collisions (e.g, `Parent/Tool.1`, `Parent.3/Tool.2` `Parent3/Tool.3` )

#### Description JSON

The description field needs to contain the following entries. The JSON object can be extended vendor-specifically if necessary.

| Name                 | DataType       | Dimension | ModellingRule | Description                                                     |
|----------------------|----------------|-----------|---------------|-----------------------------------------------------------------|
| Type                 | QualifiedName     | Array    | optional      | Name of the type of the ObjectType or VariableType. Each empty in the array represents a supertype of the node. The first entry is the type itself.              |
| TypeNodeID           | ExtendedNodeId | Scalar    |               | NodeId of the type of the ObjectType or VariableType            |
| AdditionalBrowsePath | String         | Scalar    | optional      | If additional BrowsePaths exist, they can be included here      |
| AdditionalReference  | KeyValuePair   | Array     | optional      | Additional references from the object, e.g., GenerateEvent      |
| Description          | String         | Scalar    | optional      | Description as defined for the description field in Part 14     |

#### DataSetMeta Example

```json
{
  "Namespaces": [
    "http://opcfoundation.org/UA/",
    "urn:DEMO-5:UA Sample Server",
    "http://opcfoundation.org/UA/DI/",
    "http://opcfoundation.org/UA/Machinery/",
    "http://opcfoundation.org/UA/IA/",
    "http://opcfoundation.org/UA/MachineTool/",
    "urn:Demo:MachineTool:myMachine/"
  ],
  "StructureDataTypes": "...",
  "Name": "5:Production.5:ActiveProgram.5:State",
	"Description": "{\r\n
    \"Type\": \"5:ProductionProgramStateMachineType\",\r\n
    \"TypeNodeID\": \"nsu=http:\/\/opcfoundation.org\/UA\/Machinery\/;i=58997\",\r\n
    \"AdditionalReference\": [\r\n
	{\"GeneratesEvents\":\"nsu=http:\/\/opcfoundation.org\/UA\/Machinery\/;i=3444\"}\r\n
    ]\r\n
	}"
  "Fields": [{
    "Name": "http://opcfoundation.org/UA/Machinery/#name",
    "Description": "as in Part 14",
    "FieldFlags": "as in Part 14",
    "BuiltInType": "12",
    "DataType": {"id": 12},
    "ValueRank": -1,
    "ArrayDimensions": "as in Part 14",
    "MaxStringLength": "as in Part 14",
    "DataSetFieldId": "as in Part 14",
    "Properties": "not used"
  },{
    "Name": "http://opcfoundation.org/UA/Machinery/#NumberInList",
    "Description": "as in Part 14",
    "FieldFlags": "as in Part 14",
    "BuiltInType": "5",
    "DataType": {"id": 5},
    "ValueRank": -1,
    "ArrayDimensions": "as in Part 14",
    "MaxStringLength": "as in Part 14",
    "DataSetFieldId": "as in Part 14",
    "Properties": "not used"
  }]
}
```

#### DateSet

The DataSet follows the MetaData and other definitions of the specification. A DataSet for the umati Dashboard needs to contain at least the Payload as RowData and a DataSet message header with the following fields:

- Timestamp
- Status
- Name

#### DateSet Example

```json
{
  "Timestamp": "2021-09-27T18:45:19.555Z",
  "Status": 1073741824,
  "Name": "5:Production.5:ActiveProgram.5:State",
  "Payload": {
    "Name": "Basic Program",
    "NumberInList": 0
  }
}
```
