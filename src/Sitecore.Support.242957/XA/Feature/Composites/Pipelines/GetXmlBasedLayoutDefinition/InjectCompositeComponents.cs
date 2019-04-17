namespace Sitecore.Support.XA.Feature.Composites.Pipelines.GetXmlBasedLayoutDefinition
{
  using Microsoft.Extensions.DependencyInjection;
  using Sitecore.Data;
  using Sitecore.DependencyInjection;
  using Sitecore.Diagnostics;
  using Sitecore.Extensions.XElementExtensions;
  using Sitecore.Layouts;
  using Sitecore.Mvc.Analytics.Pipelines.Response.CustomizeRendering;
  using Sitecore.Mvc.Extensions;
  using Sitecore.Mvc.Pipelines.Response.GetXmlBasedLayoutDefinition;
  using Sitecore.Pipelines;
  using Sitecore.Pipelines.ResolveRenderingDatasource;
  using Sitecore.Text;
  using Sitecore.XA.Feature.Composites.Extensions;
  using Sitecore.XA.Feature.Composites.Services;
  using Sitecore.XA.Foundation.Caching;
  using Sitecore.XA.Foundation.LocalDatasources.Services;
  using Sitecore.XA.Foundation.Multisite;
  using Sitecore.XA.Foundation.Presentation.Layout;
  using System;
  using System.Collections.Generic;
  using System.Collections.Specialized;
  using System.Linq;
  using System.Xml;
  using System.Xml.Linq;
  public class InjectCompositeComponents : GetFromLayoutField
  {
    protected static readonly DictionaryCache DictionaryCacheInstance = new DictionaryCache("SXA[CompositesXml]", StringUtil.ParseSizeString(Sitecore.Configuration.Settings.GetSetting("XA.Feature.Composites.CompositesXmlCacheMaxSize", "50MB")));

    protected DictionaryCache DictionaryCache
    {
      get
      {
        return InjectCompositeComponents.DictionaryCacheInstance;
      }
    }

    public override void Process(GetXmlBasedLayoutDefinitionArgs args)
    {
      Sitecore.Data.Items.Item obj = args.ContextItem ?? args.PageContext.Item ?? Sitecore.Mvc.Presentation.PageContext.Current.Item;
      XElement result = args.Result;
      if (result == null || !obj.Paths.IsContentItem)
        return;
      //Sitecore.Data.Items.Item siteItem = ServiceLocator.ServiceProvider.GetService<IMultisiteContext>().GetSiteItem(obj);
      Sitecore.Data.Items.Item siteItem = ServiceLocator.ServiceProvider.GetService<IMultisiteContext>().GetSiteItem(obj);
      if (siteItem == null)
        return;
      List<XElement> compositeComponents = this.GetCompositeComponents(result);
      if (!compositeComponents.Any<XElement>())
        return;
      DictionaryCacheValue dictionaryCacheValue = this.DictionaryCache.Get(this.CreateCompositesXmlCacheKey(obj.ID, siteItem.ID));
      if (Context.PageMode.IsNormal && dictionaryCacheValue != null && dictionaryCacheValue.Properties.ContainsKey((object)"CompositesXml"))
      {
        args.Result = XElement.Parse(dictionaryCacheValue.Properties[(object)"CompositesXml"].ToString());
      }
      else
      {
        if (!args.CustomData.ContainsKey("sxa-composite-recursion-level"))
          args.CustomData.Add("sxa-composite-recursion-level", (object)1);
        else
          args.CustomData["sxa-composite-recursion-level"] = (object)((int)args.CustomData["sxa-composite-recursion-level"] + 1);
        foreach (XElement rendering in compositeComponents)
          this.ProcessCompositeComponent(args, rendering, result);
        List<XElement> list = result.Descendants((XName)"d").ToList<XElement>();
        args.Result.Descendants((XName)"d").Remove<XElement>();
        args.Result.Add((object)list);
        if (!Context.PageMode.IsNormal)
          return;
        this.StoreValueInCache(this.CreateCompositesXmlCacheKey(obj.ID, siteItem.ID), args.Result.ToString());
      }
    }

    public virtual List<XElement> GetCompositeComponents(XElement layoutXml)
    {
      List<XElement> xelementList = new List<XElement>();
      List<XElement> devices = this.GetDevices(layoutXml, Context.Device.ID);
      ICompositeService service = ServiceLocator.ServiceProvider.GetService<ICompositeService>();
      foreach (XContainer xcontainer in devices)
      {
        foreach (XElement descendant in xcontainer.Descendants((XName)"r"))
        {
          ID id = this.IdAttribute(descendant);
          if (id != ID.Null && service.IsCompositeRendering(id, (Database)null))
            xelementList.Add(descendant);
        }
      }
      return xelementList;
    }

    public virtual List<XElement> GetDevices(XElement layoutXml, ID contextDeviceId)
    {
      List<XElement> source = this.FilterDevicesByDeviceId(layoutXml.Descendants((XName)"d").ToList<XElement>(), contextDeviceId);
      if (!source.Any<XElement>() && Context.Device.FallbackDevice != null)
        return this.GetDevices(layoutXml, Context.Device.FallbackDevice.ID);
      return source;
    }

    public virtual List<XElement> FilterDevicesByDeviceId(
      List<XElement> devices,
      ID deviceId)
    {
      return devices.Where<XElement>((Func<XElement, bool>)(element => new ID(element.GetAttributeValue("id")).Equals(deviceId))).ToList<XElement>();
    }

    protected virtual void ProcessCompositeComponent(
      GetXmlBasedLayoutDefinitionArgs args,
      XElement rendering,
      XElement layoutXml)
    {
      XmlNode xmlNode = rendering.ToXmlNode();
      Sitecore.XA.Foundation.Presentation.Layout.RenderingModel renderingModel = new Sitecore.XA.Foundation.Presentation.Layout.RenderingModel(xmlNode);
      Sitecore.Data.Items.Item contextItem = args.PageContext.Item;
      string datasource = new RenderingReference(xmlNode, contextItem.Language, contextItem.Database).Settings.Rules.Count > 0 ? this.GetCustomizedRenderingDataSource(renderingModel, contextItem) : renderingModel.DataSource;
      if (string.IsNullOrEmpty(datasource))
      {
        Log.Warn("Composite component datasource is empty", (object)rendering);
      }
      else
      {
        if (datasource.StartsWith("local:"))
          contextItem = this.SwitchContextItem(rendering, contextItem);
        if (datasource.StartsWith("page:"))
          contextItem = Context.Item;
        Sitecore.Data.Items.Item obj = this.ResolveCompositeDatasource(datasource, contextItem);
        if (obj == null)
        {
          Log.Error("Composite component has a reference to non-existing datasource : " + datasource, (object)this);
        }
        else
        {
          int num = this.DetectDatasourceLoop(args, obj) ? 1 : 0;
          int dynamicPlaceholderId = this.ExtractDynamicPlaceholderId(args, rendering);
          string empty = string.Empty;
          if (rendering.Attribute((XName)"sid") != null)
            empty = rendering.Attribute((XName)"sid").Value;
          string placeholder = renderingModel.Placeholder;
          if (num == 0)
          {
            foreach (KeyValuePair<int, Sitecore.Data.Items.Item> composite in ServiceLocator.ServiceProvider.GetService<ICompositeService>().GetCompositeItems(obj).Select<Sitecore.Data.Items.Item, KeyValuePair<int, Sitecore.Data.Items.Item>>((Func<Sitecore.Data.Items.Item, int, KeyValuePair<int, Sitecore.Data.Items.Item>>)((item, idx) => new KeyValuePair<int, Sitecore.Data.Items.Item>(idx + 1, item))).ToList<KeyValuePair<int, Sitecore.Data.Items.Item>>())
            {
              if (!this.TryMergeComposites(args, rendering, layoutXml, composite, dynamicPlaceholderId, placeholder, empty))
                break;
            }
          }
          else
            this.AbortRecursivePipeline(args, rendering);
          this.RollbackAntiLoopCollection(args, obj);
        }
      }
    }

    protected virtual int ExtractDynamicPlaceholderId(
      GetXmlBasedLayoutDefinitionArgs args,
      XElement rendering)
    {
      Sitecore.XA.Foundation.Presentation.Layout.RenderingModel rm = new Sitecore.XA.Foundation.Presentation.Layout.RenderingModel(rendering.ToXmlNode());
      string name = "DynamicPlaceholderId";
      int dynamicPlaceholderId;
      if (rm.Parameters[name] != null)
      {
        dynamicPlaceholderId = MainUtil.GetInt(rm.Parameters[name], 1);
      }
      else
      {
        dynamicPlaceholderId = this.GetDynamicPlaceholderId(args, rm);
        rm.Parameters.Add(name, dynamicPlaceholderId.ToString());
        rendering.SetAttributeValue((XName)"par", (object)new UrlString(rm.Parameters).ToString());
      }
      return dynamicPlaceholderId;
    }

    protected virtual int GetDynamicPlaceholderId(
      GetXmlBasedLayoutDefinitionArgs args,
      Sitecore.XA.Foundation.Presentation.Layout.RenderingModel rm)
    {
      DeviceModel deviceModel1 = new LayoutModel(args.Result.ToString()).Devices.DevicesCollection.FirstOrDefault<DeviceModel>((Func<DeviceModel, bool>)(deviceModel => deviceModel.Renderings.RenderingsCollection.Any<Sitecore.XA.Foundation.Presentation.Layout.RenderingModel>((Func<Sitecore.XA.Foundation.Presentation.Layout.RenderingModel, bool>)(r => r.UniqueId.Equals(rm.UniqueId)))));
      if (deviceModel1 == null)
        return 1;
      int num = 0;
      foreach (Sitecore.XA.Foundation.Presentation.Layout.RenderingModel renderings in deviceModel1.Renderings.RenderingsCollection)
      {
        NameValueCollection parameters = renderings.Parameters;
        int result;
        if (parameters != null && ((IEnumerable<string>)parameters.AllKeys).Contains<string>("DynamicPlaceholderId") && (int.TryParse(parameters["DynamicPlaceholderId"], out result) && result > num))
          num = result;
      }
      return num + 1;
    }

    protected virtual string GetCustomizedRenderingDataSource(
      Sitecore.XA.Foundation.Presentation.Layout.RenderingModel renderingModel,
      Sitecore.Data.Items.Item contextItem)
    {
      ID id = renderingModel.Id;
      string path = contextItem.Database.GetItem(id).Paths.Path;
      CustomizeRenderingArgs customizeRenderingArgs = new CustomizeRenderingArgs(new Sitecore.Mvc.Presentation.Rendering()
      {
        RenderingItemPath = path,
        Properties = {
          ["RenderingXml"] = renderingModel.ToString()
        }
      });
      CorePipeline.Run("mvc.customizeRendering", (PipelineArgs)customizeRenderingArgs, false);
      return customizeRenderingArgs.Rendering.DataSource;
    }

    protected virtual Sitecore.Data.Items.Item SwitchContextItem(
      XElement rendering,
      Sitecore.Data.Items.Item contextItem)
    {
      ID result;
      if (ID.TryParse(rendering.GetAttributeValue("sid"), out result))
        contextItem = contextItem.Database.GetItem(result);
      return contextItem;
    }

    protected virtual void ResolveLocalDatasources(
      IEnumerable<XElement> renderings,
      Sitecore.Data.Items.Item compositeDataItem)
    {
      foreach (XElement rendering in renderings)
      {
        XAttribute xattribute = rendering.Attribute((XName)"ds");
        if (rendering.HasAttributes && xattribute != null)
        {
          string relativePath = xattribute.Value;
          if (relativePath.StartsWith("local:"))
          {
            string str = ServiceLocator.ServiceProvider.GetService<ILocalDatasourceService>().ExpandPageRelativePath(relativePath, compositeDataItem.Paths.FullPath);
            rendering.SetAttributeValue((XName)"ds", (object)str);
          }
        }
      }
    }

    protected virtual bool TryMergeComposites(
      GetXmlBasedLayoutDefinitionArgs args,
      XElement rendering,
      XElement layoutXml,
      KeyValuePair<int, Sitecore.Data.Items.Item> composite,
      int dynamicPlaceholderId,
      string parentPh,
      string partialDesignId)
    {
      bool loopDetected;
      List<XElement> compositeRenderings = this.GetCompositeRenderings(args, composite, out loopDetected);
      if (loopDetected)
      {
        this.AbortRecursivePipeline(args, rendering);
        return false;
      }
      this.ResolveLocalDatasources((IEnumerable<XElement>)compositeRenderings, composite.Value);
      this.MergeComposites(layoutXml, compositeRenderings, composite, dynamicPlaceholderId, parentPh, partialDesignId);
      return true;
    }

    protected virtual void AbortRecursivePipeline(
      GetXmlBasedLayoutDefinitionArgs args,
      XElement rendering)
    {
      this.SetTrueAttribute(rendering, "cmps-loop");
      if ((int)args.CustomData["sxa-composite-recursion-level"] == 1)
        return;
      this.AbortPipeline(args);
    }

    protected virtual bool DetectDatasourceLoop(
      GetXmlBasedLayoutDefinitionArgs args,
      Sitecore.Data.Items.Item datasource)
    {
      bool flag = false;
      if (args.CustomData.ContainsKey("sxa-composite-antiloop"))
      {
        HashSet<ID> idSet = args.CustomData["sxa-composite-antiloop"] as HashSet<ID>;
        if (idSet != null)
        {
          if (!idSet.Contains(datasource.ID))
          {
            idSet.Add(datasource.ID);
            args.CustomData["sxa-composite-antiloop"] = (object)idSet;
          }
          else
            flag = true;
        }
      }
      else if (Context.Item.Parent.ID == datasource.ID)
        flag = true;
      else
        args.CustomData.Add("sxa-composite-antiloop", (object)new HashSet<ID>((IEnumerable<ID>)new ID[1]
        {
          datasource.ID
        }));
      return flag;
    }

    protected virtual void RollbackAntiLoopCollection(
      GetXmlBasedLayoutDefinitionArgs args,
      Sitecore.Data.Items.Item datasource)
    {
      if (!args.CustomData.ContainsKey("sxa-composite-antiloop"))
        return;
      HashSet<ID> idSet = args.CustomData["sxa-composite-antiloop"] as HashSet<ID>;
      if (idSet == null || !idSet.Contains(datasource.ID))
        return;
      idSet.Remove(datasource.ID);
    }

    protected virtual void AbortPipeline(GetXmlBasedLayoutDefinitionArgs args)
    {
      if (!args.CustomData.ContainsKey("sxa-composite-loop-detected"))
        args.CustomData.Add("sxa-composite-loop-detected", (object)true);
      args.AbortPipeline();
    }

    protected virtual Sitecore.Data.Items.Item ResolveCompositeDatasource(string datasource)
    {
      return this.ResolveCompositeDatasource(datasource, (Sitecore.Data.Items.Item)null);
    }

    protected virtual Sitecore.Data.Items.Item ResolveCompositeDatasource(
      string datasource,
      Sitecore.Data.Items.Item contextItem)
    {
      ID result;
      if (ID.TryParse(datasource, out result))
        return Context.Database.Items[result];
      ResolveRenderingDatasourceArgs renderingDatasourceArgs = new ResolveRenderingDatasourceArgs(datasource);
      if (contextItem != null)
        renderingDatasourceArgs.CustomData.Add(nameof(contextItem), (object)contextItem);
      CorePipeline.Run("resolveRenderingDatasource", (PipelineArgs)renderingDatasourceArgs);
      return Context.Database.GetItem(renderingDatasourceArgs.Datasource);
    }

    protected virtual void MergeComposites(
      XElement layoutXml,
      List<XElement> compositeRenderings,
      KeyValuePair<int, Sitecore.Data.Items.Item> composite,
      int dynamicPlaceholderId,
      string parentPh,
      string partialDesignId)
    {
      compositeRenderings.Where<XElement>(new Func<XElement, bool>(this.IsValidRenderingNode)).ToList<XElement>().ForEach((Action<XElement>)(compositeRendering =>
      {
        string relativePlaceholder = this.GetRelativePlaceholder(compositeRendering, composite, dynamicPlaceholderId, parentPh);
        compositeRendering.Attribute((XName)"ph")?.SetValue((object)relativePlaceholder);
        this.SetAttribute(compositeRendering, "cmps-item", (object)composite.Value.ID);
        this.SetTrueAttribute(compositeRendering, "cmps");
        this.HandleEmptyDatasources(composite.Value, compositeRendering);
        this.SetPartialDesignId(compositeRendering, partialDesignId);
        this.SetOriginalDataSource(compositeRendering);
      }));
      foreach (XElement xelement in layoutXml.Descendants((XName)"d").GroupBy<XElement, string>((Func<XElement, string>)(element => element.GetAttributeValue("id"))).Select<IGrouping<string, XElement>, XElement>((Func<IGrouping<string, XElement>, XElement>)(elements => elements.First<XElement>())))
      {
        XElement device = xelement;
        DeviceModel deviceModel = new DeviceModel(device.ToXmlNode());
        compositeRenderings = compositeRenderings.Where<XElement>((Func<XElement, bool>)(e => this.NotInjectedIntoDevice(new Sitecore.XA.Foundation.Presentation.Layout.RenderingModel(e.ToXmlNode()), deviceModel))).ToList<XElement>();
        compositeRenderings.ForEach((Action<XElement>)(compositeRendering => device.Add((object)compositeRendering)));
      }
    }

    protected virtual bool IsValidRenderingNode(XElement arg)
    {
      if (arg.HasAttributes && arg.AttributeNotNull("ph"))
        return arg.AttributeNotNull("id");
      return false;
    }

    protected virtual bool NotInjectedIntoDevice(Sitecore.XA.Foundation.Presentation.Layout.RenderingModel rm, DeviceModel dm)
    {
      return dm.Renderings.RenderingsCollection.All<Sitecore.XA.Foundation.Presentation.Layout.RenderingModel>((Func<Sitecore.XA.Foundation.Presentation.Layout.RenderingModel, bool>)(r =>
      {
        if (!(r.UniqueId != rm.UniqueId))
          return r.Placeholder != rm.Placeholder;
        return true;
      }));
    }

    private void SetAttribute(XElement composite, string attribute, object value)
    {
      if (composite.Attribute((XName)attribute) != null)
        return;
      XAttribute xattribute = new XAttribute((XName)attribute, value);
      composite.Add((object)xattribute);
    }

    protected virtual void HandleEmptyDatasources(Sitecore.Data.Items.Item compositeItem, XElement compositeRendering)
    {
      if (compositeRendering.Attribute((XName)"id") == null)
        return;
      string str = compositeItem.ID.ToString();
      if (compositeRendering.Attribute((XName)"ds") == null)
      {
        XAttribute xattribute = new XAttribute((XName)"ds", (object)str);
        compositeRendering.Add((object)xattribute);
      }
      else
      {
        if (!string.IsNullOrEmpty(compositeRendering.Attribute((XName)"ds").Value))
          return;
        compositeRendering.Attribute((XName)"ds").SetValue((object)str);
      }
    }

    protected virtual void SetTrueAttribute(XElement composite, string attribute)
    {
      if (composite.Attribute((XName)attribute) != null)
        return;
      XAttribute xattribute = new XAttribute((XName)attribute, (object)"true");
      composite.Add((object)xattribute);
    }

    protected virtual List<XElement> GetCompositeRenderings(
      GetXmlBasedLayoutDefinitionArgs args,
      KeyValuePair<int, Sitecore.Data.Items.Item> composite,
      out bool loopDetected)
    {
      GetXmlBasedLayoutDefinitionArgs layoutDefinitionArgs = this.PreparePipelineArgs(args, composite);
      CorePipeline.Run("mvc.getCompositeXmlBasedLayoutDefinition", (PipelineArgs)layoutDefinitionArgs);
      List<XElement> xelementList = new List<XElement>();
      if (!layoutDefinitionArgs.CustomData.ContainsKey("sxa-composite-loop-detected"))
      {
        XElement deviceDefinition = this.GetValidDeviceDefinition(layoutDefinitionArgs.Result, layoutDefinitionArgs.PageContext.Device.DeviceItem.ID);
        if (deviceDefinition == null && layoutDefinitionArgs.PageContext.Device.DeviceItem.FallbackDevice != null)
          deviceDefinition = this.GetValidDeviceDefinition(layoutDefinitionArgs.Result, layoutDefinitionArgs.PageContext.Device.DeviceItem.FallbackDevice.ID);
        xelementList = deviceDefinition == null ? layoutDefinitionArgs.Result.Descendants((XName)"r").ToList<XElement>() : deviceDefinition.Descendants((XName)"r").ToList<XElement>();
      }
      loopDetected = layoutDefinitionArgs.Aborted && layoutDefinitionArgs.CustomData.ContainsKey("sxa-composite-loop-detected");
      return xelementList;
    }

    protected virtual XElement GetValidDeviceDefinition(XElement layout, ID deviceId)
    {
      return layout.Descendants((XName)"d").FirstOrDefault<XElement>((Func<XElement, bool>)(e =>
      {
        string attributeValueOrNull = e.GetAttributeValueOrNull("id");
        if (e.GetAttributeValueOrNull("l") != null && attributeValueOrNull != null)
          return new ID(attributeValueOrNull).Equals(deviceId);
        return false;
      }));
    }

    protected virtual GetXmlBasedLayoutDefinitionArgs PreparePipelineArgs(
      GetXmlBasedLayoutDefinitionArgs args,
      KeyValuePair<int, Sitecore.Data.Items.Item> composite)
    {
      Sitecore.Mvc.Presentation.PageContext pageContext1 = args.PageContext;
      Sitecore.Mvc.Presentation.PageContext pageContext2 = new Sitecore.Mvc.Presentation.PageContext()
      {
        Device = pageContext1.Device,
        Database = pageContext1.Database,
        Item = composite.Value,
        RequestContext = pageContext1.RequestContext
      };
      GetXmlBasedLayoutDefinitionArgs layoutDefinitionArgs1 = new GetXmlBasedLayoutDefinitionArgs();
      layoutDefinitionArgs1.PageContext = pageContext2;
      layoutDefinitionArgs1.ProcessorItem = args.ProcessorItem;
      GetXmlBasedLayoutDefinitionArgs layoutDefinitionArgs2 = layoutDefinitionArgs1;
      layoutDefinitionArgs2.CustomData.AddRange(args.CustomData);
      if (!layoutDefinitionArgs2.CustomData.ContainsKey("sxa-composite"))
        layoutDefinitionArgs2.CustomData.Add("sxa-composite", (object)composite.Value);
      else
        layoutDefinitionArgs2.CustomData["sxa-composite"] = (object)composite.Value;
      return layoutDefinitionArgs2;
    }

    protected virtual string GetRelativePlaceholder(
      XElement compositeRendering,
      KeyValuePair<int, Sitecore.Data.Items.Item> composite,
      int dynamicPlaceholderId,
      string parentPh)
    {
      XAttribute xattribute = compositeRendering.Attribute((XName)"ph");
      if (xattribute == null)
        return string.Empty;
      string str1 = xattribute.Value;
      parentPh = parentPh.StartsWith("/") ? parentPh : "/" + parentPh;
      string str2;
      if (str1.StartsWith("/"))
      {
        string[] strArray = str1.Split('/');
        strArray[1] = string.Format("{0}-{1}-{2}", (object)strArray[1], (object)composite.Key, (object)dynamicPlaceholderId);
        str2 = parentPh + string.Join("/", strArray);
      }
      else
      {
        string str3 = string.Format("{0}-{1}-{2}", (object)str1, (object)composite.Key, (object)dynamicPlaceholderId);
        str2 = parentPh + "/" + str3;
      }
      return str2;
    }

    protected void SetOriginalDataSource(XElement compositeRendering)
    {
      if (!compositeRendering.HasAttributes || compositeRendering.Attribute((XName)"ds") == null || compositeRendering.Attribute((XName)"ods") != null)
        return;
      string str = compositeRendering.Attribute((XName)"ds").Value;
      compositeRendering.Add((object)new XAttribute((XName)"ods", (object)str));
    }

    protected void SetPartialDesignId(XElement compositeRendering, string partialDesignId)
    {
      if (string.IsNullOrEmpty(partialDesignId))
        return;
      compositeRendering.Add((object)new XAttribute((XName)"sid", (object)partialDesignId));
    }

    protected ID IdAttribute(XElement cmps)
    {
      XAttribute xattribute = cmps.Attribute((XName)"id");
      if (xattribute != null)
        return ID.Parse(xattribute.Value);
      Log.Error(string.Format("SXA: Could not find 'id' attribute for node {0}", (object)cmps), (object)this);
      return ID.Null;
    }

    protected string CreateCompositesXmlCacheKey(ID itemId, ID siteId)
    {
      return string.Format("{0}::{1}::{2}::{3}::{4}::{5}", (object)"SXA::CompositesXml", (object)siteId, (object)Context.Database.Name, (object)Context.Device.ID, (object)Context.Language.Name, (object)itemId);
    }

    protected virtual void StoreValueInCache(string cacheKey, string value)
    {
      this.DictionaryCache.Set(cacheKey, new DictionaryCacheValue()
      {
        Properties = {
          [(object) "CompositesXml"] = (object) value
        }
      });
    }
  }
}
