using Google.Protobuf;
using Onnx;

namespace Bilgames.Tts.Kokoro;

/// <summary>
/// Makes the Kokoro graph runnable on DirectML. DirectML rejects Kokoro's three depthwise 1-D
/// ConvTranspose upsampling layers (kernel 3, stride 2, pad 1, output_padding 1, group = channels),
/// so each is rewritten as the mathematically equivalent zero-insertion upsample followed by a
/// grouped Conv with the kernel flipped — operations DirectML supports.
/// </summary>
public static class ModelPatcher
{
    public static int PatchForDirectML(string source, string destination)
    {
        var model = ModelProto.Parser.ParseFrom(File.ReadAllBytes(source));
        var g = model.Graph;
        int patched = 0;

        for (int i = 0; i < g.Node.Count; i++)
        {
            var n = g.Node[i];
            if (n.OpType != "ConvTranspose") continue;
            long group = n.Attribute.FirstOrDefault(a => a.Name == "group")?.I ?? 1;
            if (group <= 1) continue;
            var strides = n.Attribute.First(a => a.Name == "strides").Ints;
            var kernel = n.Attribute.First(a => a.Name == "kernel_shape").Ints[0];
            var pads = n.Attribute.First(a => a.Name == "pads").Ints;
            var outPad = n.Attribute.FirstOrDefault(a => a.Name == "output_padding")?.Ints[0] ?? 0;
            if (strides[0] != 2 || kernel != 3 || pads[0] != 1 || pads[1] != 1 || outPad != 1) continue; // only the known shape

            // Conv uses the flipped kernel of the transposed convolution (depthwise: [C,1,3] either way).
            var w = g.Initializer.First(t => t.Name == n.Input[1]);
            var flipped = w.Clone();
            flipped.Name = w.Name + ".flipped";
            int elem = w.DataType == (int)TensorProto.Types.DataType.Float16 ? 2 : 4;
            var raw = w.RawData.ToByteArray();
            var dst = new byte[raw.Length];
            for (long c = 0; c < w.Dims[0]; c++)
                for (int j = 0; j < 3; j++)
                    Buffer.BlockCopy(raw, (int)((c * 3 + j) * elem), dst, (int)((c * 3 + (2 - j)) * elem), elem);
            flipped.RawData = ByteString.CopyFrom(dst);
            g.Initializer.Add(flipped);

            string p = n.Name.Replace("/ConvTranspose", "/dw");
            var axes = new TensorProto { Name = p + "/axes", DataType = (int)TensorProto.Types.DataType.Int64 };
            axes.Dims.Add(1);
            axes.Int64Data.Add(3);
            var shape = new TensorProto { Name = p + "/shape", DataType = (int)TensorProto.Types.DataType.Int64 };
            shape.Dims.Add(3);
            shape.Int64Data.AddRange(new long[] { 0, 0, -1 });
            g.Initializer.Add(axes);
            g.Initializer.Add(shape);

            // x[N,C,L] → [N,C,L,1] ++ zeros → [N,C,L,2] → [N,C,2L]: x0 0 x1 0 … (trailing zero acts as output_padding)
            var unsq = Node("Unsqueeze", p + "/unsq", new[] { n.Input[0], axes.Name }, p + "/unsq_out");
            var zero = Node("Sub", p + "/zero", new[] { p + "/unsq_out", p + "/unsq_out" }, p + "/zero_out");
            var cat = Node("Concat", p + "/cat", new[] { p + "/unsq_out", p + "/zero_out" }, p + "/cat_out");
            cat.Attribute.Add(new AttributeProto { Name = "axis", I = 3, Type = AttributeProto.Types.AttributeType.Int });
            var reshape = Node("Reshape", p + "/rs", new[] { p + "/cat_out", shape.Name }, p + "/up");
            var conv = Node("Conv", p + "/conv", new[] { p + "/up", flipped.Name, n.Input[2] }, n.Output[0]);
            conv.Attribute.Add(new AttributeProto { Name = "group", I = group, Type = AttributeProto.Types.AttributeType.Int });
            var k = new AttributeProto { Name = "kernel_shape", Type = AttributeProto.Types.AttributeType.Ints };
            k.Ints.Add(3);
            var pd = new AttributeProto { Name = "pads", Type = AttributeProto.Types.AttributeType.Ints };
            pd.Ints.AddRange(new long[] { 1, 1 });
            conv.Attribute.Add(k);
            conv.Attribute.Add(pd);

            g.Node.RemoveAt(i);
            g.Node.Insert(i, conv);
            g.Node.Insert(i, reshape);
            g.Node.Insert(i, cat);
            g.Node.Insert(i, zero);
            g.Node.Insert(i, unsq);
            i += 4;
            patched++;
        }

        var tmp = destination + ".tmp";
        File.WriteAllBytes(tmp, model.ToByteArray());
        File.Move(tmp, destination, overwrite: true);
        return patched;
    }

    private static NodeProto Node(string type, string name, string[] inputs, string output)
    {
        var node = new NodeProto { OpType = type, Name = name };
        node.Input.AddRange(inputs);
        node.Output.Add(output);
        return node;
    }
}
