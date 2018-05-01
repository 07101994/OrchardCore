using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Razor.TagHelpers;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.CodeAnalysis;

namespace OrchardCore.DisplayManagement.Liquid.Tags.TagHelpers
{
    /// <summary>
    /// Singleton containing shared state for tag helper tags.
    /// </summary>
    public class TagHelperSharedState
    {
        private static ConcurrentDictionary<Type, TagHelperMatching> _sharedMatchings = new ConcurrentDictionary<Type, TagHelperMatching>();
        private static ConcurrentDictionary<Type, TagHelperActivator> _activators = new ConcurrentDictionary<Type, TagHelperActivator>();

        private List<TagHelperMatching> _matchings;
        private readonly ApplicationPartManager _applicationPartManager;

        public TagHelperSharedState(ApplicationPartManager applicationPartManager)
        {
            _applicationPartManager = applicationPartManager;
        }

        private void EnsureMatchings()
        {
            if (_matchings != null)
            {
                return;
            }

            lock (this)
            {
                if (_matchings == null)
                {
                    var feature = new TagHelperFeature();
                    _applicationPartManager.PopulateFeature(feature);

                    var matchings = new List<TagHelperMatching>();

                    foreach (var tagHelper in feature.TagHelpers)
                    {
                        var matching = _sharedMatchings.GetOrAdd(tagHelper.AsType(), type =>
                        {
                            var descriptorBuilder = TagHelperDescriptorBuilder.Create(
                                type.FullName, type.Assembly.GetName().Name);

                            descriptorBuilder.SetTypeName(type.FullName);
                            AddTagMatchingRules(type, descriptorBuilder);
                            var descriptor = descriptorBuilder.Build();

                            return new TagHelperMatching(
                                descriptor.Name,
                                descriptor.AssemblyName,
                                descriptor.TagMatchingRules
                            );
                        });

                        matchings.Add(matching);
                    }

                    _matchings = matchings;
                }
            }
        }

        public TagHelperMatching GetMatching(string helper, IEnumerable<string> arguments)
        {
            EnsureMatchings();
            return _matchings.Where(d => d.Match(helper, arguments)).FirstOrDefault() ?? TagHelperMatching.None;
        }

        public static TagHelperActivator GetActivator(TagHelperMatching matching)
        {
            var tagHelperType = Type.GetType(matching.Name + ", " + matching.AssemblyName);
            return _activators.GetOrAdd(tagHelperType, type => new TagHelperActivator(type));
        }

        private static void AddTagMatchingRules(Type type, TagHelperDescriptorBuilder descriptorBuilder)
        {
            var targetElementAttributes = type.GetCustomAttributes<HtmlTargetElementAttribute>();

            // If there isn't an attribute specifying the tag name derive it from the name
            if (!targetElementAttributes.Any())
            {
                var name = type.Name;

                if (name.EndsWith("TagHelper", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - "TagHelper".Length);
                }

                descriptorBuilder.TagMatchingRule(ruleBuilder =>
                {
                    var htmlCasedName = HtmlConventions.ToHtmlCase(name);
                    ruleBuilder.TagName = htmlCasedName;
                });

                return;
            }

            foreach (var targetElementAttribute in targetElementAttributes)
            {
                descriptorBuilder.TagMatchingRule(ruleBuilder =>
                {
                    var tagName = targetElementAttribute.Tag;
                    ruleBuilder.TagName = tagName;

                    var parentTag = targetElementAttribute.ParentTag;
                    ruleBuilder.ParentTag = parentTag;

                    var tagStructure = targetElementAttribute.TagStructure;
                    ruleBuilder.TagStructure = (Microsoft.AspNetCore.Razor.Language.TagStructure)tagStructure;

                    var requiredAttributeString = targetElementAttribute.Attributes;
                    RequiredAttributeParser.AddRequiredAttributes(requiredAttributeString, ruleBuilder);
                });
            }
        }
    }
}
