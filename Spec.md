# OPC UA PubSub Interface Description

This document defines the Interface of the umati Dashboard based on OPC UA PubSub.
It describes how a OPC UA server address space must be mapped to OPC UA PubSub, so that the information can map to the templates and displayed.

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

## Design Decisions

The concept of OPC UA Part 14 should be followed as closely as possible.

## Mapping

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

## Mapping of References

The name of a DataSet is created by the BrowsePath of the object. The NamespaceUri is used instead of the NamespaceIndex to make the path server-independent. Other BrowsePaths may also be possible. Alternative BrowsePaths can be mapped to the description (see description field of the MetaData). References can also be mapped into the description.

## Mapping of Events

Events are also mapped to a DataSet. Cause Event have no BrowsePath, the BrowsePath of the SourceName and the EventName is used. The mapping itself is analogous to the mapping of objects.

## Topic Structure

The generic topic structure for OPC UA is:

`<Prefix>/<Encoding>/<MqttMessageType>/<PublisherId>/[<WriterGroup>[/<DataSetWriter>]]`

For the umati Dashboard, this topic structure is restricted as follows:

- `<Prefix> = umati.v3`
- `<Encoding> = json`
- `<MqttMessageType> = as defined in OPC UA PubSub`
- `<PublisherId> = Name of the Client`
- `<WriterGroup> = Expanded NodeId of the object representing the machine`
- `<DataSetWriter> = BrowsePath`

For identification of the object, the name of the DataSet with expanded NodeId must be used.

The BrowsePath in the DataSetWriter is built with a "." between each BrowseName. The BrowseNames use only the namespace index to avoid collisions.
All character except `[A-Za-z0-9]` in the `name` field of the BrowseName  need to encode by [URL-Encoding](https://de.wikipedia.org/wiki/URL-Encoding) using an underscore instead of a '%'.
If two node has the same BrowsePath a iterator ("_Number") can be send to avoid collisions (e.g, `3:Partent.3:Tool_1`, `3:Partent.3:Tool_2` `3:Partent.3:Tool_3` )
Cause Event have no BrowsePath, the BrowsePath of the SourceName and the EventName is used.

### Examples

DataSet Topic

```text
umati.v3/json/data/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/2:Identification
umati.v3/json/data/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/3:Production.3:ActiveProgram
umati.v3/json/data/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/3:Production.3:ActiveProgram.1:State
```

DataSet Event Topic

```text
umati.v3/json/data/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/2:Identification.0:BaseEventType
```


MetaDataSet Topic

```text
umati.v3/json/metadata/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/2:Identification
umati.v3/json/metadata/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/3:Production.3:ActiveProgram
umati.v3/json/metadata/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/3:Production.3:ActiveProgram.1:State
```

### Alternative

To be discussed: Instead of building a large topic name with "." from the BrowsePath, the path can also be split into different topic levels as shown here:

```text
umati.v3/json/data/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/2:Identification
umati.v3/json/data/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/3:Production/3:ActiveProgram
umati.v3/json/data/example_publisher_1/nsu=http:_2F_2Fexample.com_2FShowcaseMachineTool_2F;i=66382/3:Production/3:ActiveProgram/1:State
```

This solution might be better for debugging but does not fit with the OPC UA PubSub specification.

## DataSetMeta

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

If two node has the same BrowsePath a iterator ("_Number") can be send to avoid collisions (e.g, `3:Partent.3:Tool_1`, `3:Partent.3:Tool_2` `3:Partent.3:Tool_3` )

### Description JSON

The description field needs to contain the following entries. The JSON object can be extended vendor-specifically if necessary.

| Name                 | DataType       | Dimension | ModellingRule | Description                                                     |
|----------------------|----------------|-----------|---------------|-----------------------------------------------------------------|
| Type                 | BrowseName     | Scalar    | optional      | Name of the type of the ObjectType or VariableType              |
| TypeNodeID           | ExtendedNodeId | Scalar    |               | NodeId of the type of the ObjectType or VariableType            |
| AdditionalBrowsePath | String         | Scalar    | optional      | If additional BrowsePaths exist, they can be included here      |
| AdditionalReference  | KeyValuePair   | Array     | optional      | Additional references from the object, e.g., GenerateEvent      |
| Description          | String         | Scalar    | optional      | Description as defined for the description field in Part 14     |

### DataSetMeta Example

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
  "Description": {
    "Type": "5:ProductionProgramStateMachineType",
    "TypeNodeID": "nsu=http://opcfoundation.org/UA/Machinery/;i=58997",
    "AdditionalReference": [
      {"GeneratesEvents":"nsu=http://opcfoundation.org/UA/Machinery/;i=3444"}
    ]
  },
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

## DateSet

The DataSet follows the MetaData and other definitions of the specification. A DataSet for the umati Dashboard needs to contain at least the Payload as RowData and a DataSet message header with the following fields:

- Timestamp
- Status

### DateSet Example

```json
{
  "Timestamp": "2021-09-27T18:45:19.555Z",
  "Status": 1073741824,
  "Payload": {
    "Name": "Basic Program",
    "NumberInList": 0
  }
}
```

## Status

As definied in OPC UA Part 14 the status should be sended at the following topic:
`umati.v3/<Encoding>/status/<PublisherId>` with  `<PublisherId>` is the name of the Client
The value of the Field "IsCyclic" should be `true` and the Status should be published at least every 90 Sec a higher frequency is possible.
## Application

The Application can be send optional.

## Connection

The Connection can be send optional.
